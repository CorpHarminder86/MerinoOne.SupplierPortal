using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// PO inbound ingestion + read-back lifecycle, through the REAL host: a Purchase Order pushed by Infor LN over
/// the X-APIKey scheme (POST /api/integration/inbound/purchase-orders) must persist, and the supplier's
/// authenticated read (GET /api/purchase-orders/{id}) must surface the line columns the procurement chain
/// depends on — currency, taxId (resolved by TaxCode against the company's pushed Tax master), discountPct,
/// and the serial/lot control flags resolved off inv.Item by ItemCode.
///
/// <para>Runs with the scope gate in its money-path OFF state (no flip). Every row carries a per-test tag so it
/// never collides with the shared seed.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PurchaseOrderInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PurchaseOrderInboundTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Inbound_po_persists_and_readback_resolves_currency_tax_and_serial_lot_flags()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // A tagged supplier (its own G-seccode) under the fixture tenant/company, granting the Supplier-role
        // API user read so the supplier client can GET its own PO once it lands.
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        // Two items honouring the inv.Item XOR CHECK: one serialized, one lot-controlled. Seeded under company
        // "2000" so the PO-line read resolves the flags by (TenantEntityId, ItemCode).
        var serialItem = await _fx.CreateItemAsync($"SER-{tag}", isSerialized: true);
        var lotItem = await _fx.CreateItemAsync($"LOT-{tag}", isLotControlled: true);

        // Seed a Tax master so the PO line's taxCode resolves to a Tax FK (taxId). Seeded directly (not via the
        // inbound /taxes endpoint) because the fixture API key does not carry the Integration.Inbound.Tax scope.
        var taxCode = $"GST18-{tag}";
        await _fx.CreateTaxAsync(taxCode, rate: 18m);

        var inbound = _fx.CreateInboundClient();

        // Push the PO with two lines: a serialized line + a lot line, each carrying a taxCode + discountPct.
        var poNumber = $"PO-INB-{tag}";
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber,
                SupplierCode: supplier.SupplierCode,
                PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: serialItem.ItemCode,
                        OrderUnit: "EA", OrderQty: 5, PriceUnit: 1, Price: 100,
                        DiscountPct: 10m, TaxCode: taxCode, TaxDescription: "GST 18%"),
                    new PoLineRecord(PositionNo: 20, SequenceNo: 2, ItemCode: lotItem.ItemCode,
                        OrderUnit: "KG", OrderQty: 40, PriceUnit: 1, Price: 25,
                        DiscountPct: 5m, TaxCode: taxCode, TaxDescription: "GST 18%"),
                },
                PoStatus: nameof(PoStatus.Released),
                CurrencyCode: "INR"),
        });

        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var pushResult = await Read<UpsertResultDto>(poResp);
        pushResult.Success.Should().BeTrue();
        pushResult.Data!.Failed.Should().Be(0, because: "the supplier code resolves, so the PO row inserts cleanly");
        pushResult.Data!.Inserted.Should().Be(1);

        // Find the persisted PO id (the inbound DTO returns per-row outcomes keyed by code, not the new Guid).
        Guid poId;
        Guid? expectedTaxId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            poId = po.Id;
            po.SupplierId.Should().Be(supplier.SupplierId, because: "the PO's owning supplier resolves by code");
            po.SeccodeId.Should().Be(supplier.SeccodeId, because: "the PO inherits the owning supplier's G-seccode for RLS");
            po.CurrencyCode.Should().Be("INR");

            expectedTaxId = await db.Taxes.IgnoreQueryFilters()
                .Where(t => t.TenantEntityId == IntegrationTestFixture.CompanyId && t.Code == taxCode)
                .Select(t => (Guid?)t.Id).FirstAsync();
        }

        // Supplier reads its own PO. The detail DTO must carry currency + each line's taxId, discountPct, and
        // the serial/lot flags resolved off inv.Item.
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var getResp = await supplierClient.GetAsync($"/api/purchase-orders/{poId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Read<PurchaseOrderDetailDto>(getResp);
        detail.Success.Should().BeTrue();
        detail.Data!.CurrencyCode.Should().Be("INR");
        detail.Data!.Lines.Should().HaveCount(2);

        var serLine = detail.Data!.Lines.Single(l => l.PositionNo == 10);
        serLine.ItemCode.Should().Be(serialItem.ItemCode);
        serLine.TaxId.Should().Be(expectedTaxId, because: "the line's taxCode resolves to the pushed Tax master FK");
        serLine.DiscountPct.Should().Be(10m);
        serLine.IsSerialized.Should().BeTrue(because: "the line item is serialized in inv.Item");
        serLine.IsLotControlled.Should().BeFalse();

        var lotLine = detail.Data!.Lines.Single(l => l.PositionNo == 20);
        lotLine.ItemCode.Should().Be(lotItem.ItemCode);
        lotLine.TaxId.Should().Be(expectedTaxId);
        lotLine.DiscountPct.Should().Be(5m);
        lotLine.IsLotControlled.Should().BeTrue(because: "the line item is lot-controlled in inv.Item");
        lotLine.IsSerialized.Should().BeFalse();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
