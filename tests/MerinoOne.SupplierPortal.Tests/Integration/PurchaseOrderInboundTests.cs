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

    /// <summary>
    /// Supplier-identity resolution on the inbound PO push (erpSupplierCode / supplierCode). Covers all four
    /// flows: (1) erpSupplierCode only → resolve by Supplier.ErpCode; (2) supplierCode only → resolve by code
    /// [covered by the test above]; (3) BOTH → erpSupplierCode WINS; (4) NEITHER → 400, nothing persists.
    /// Plus an unknown erpCode → per-row failure (200, Failed=1).
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_resolves_supplier_by_erpCode_with_priority_over_supplierCode()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // Supplier A carries an ERP code; Supplier B is a decoy that only has a supplier code — used to prove the
        // priority rule (a PO citing A's erpCode AND B's supplierCode must bind A, not B).
        var supA = await _fx.CreateSupplierAsync($"A{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var supB = await _fx.CreateSupplierAsync($"B{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var erp = $"ERP-{tag}";
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.Suppliers.IgnoreQueryFilters().FirstAsync(x => x.Id == supA.SupplierId);
            a.ErpCode = erp;
            await db.SaveChangesAsync();
        }

        var inbound = _fx.CreateInboundClient();

        PushPurchaseOrdersRequest Body(string poNum, string? supplierCode, string? erpSupplierCode) =>
            new(IntegrationTestFixture.CompanyCode, new[]
            {
                new PoRecord(
                    PoNumber: poNum,
                    SupplierCode: supplierCode,
                    PoDate: DateTime.UtcNow.Date,
                    Lines: new[]
                    {
                        new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: $"ITM-{tag}",
                            OrderUnit: "EA", OrderQty: 1, PriceUnit: 1, Price: 1),
                    },
                    PoStatus: nameof(PoStatus.Released),
                    CurrencyCode: "INR",
                    ErpSupplierCode: erpSupplierCode),
            });

        async Task<Guid?> ResolvedSupplierIdAsync(string poNum)
        {
            using var s = _fx.Factory.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(p => p.PoNumber == poNum && p.TenantId == IntegrationTestFixture.TenantId)
                .Select(p => (Guid?)p.SupplierId).FirstOrDefaultAsync();
        }

        // ---- Flow 1: erpSupplierCode only → resolves Supplier A by ErpCode. ----
        var po1 = $"PO-ERP1-{tag}";
        var resp1 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po1, supplierCode: null, erpSupplierCode: erp));
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var r1 = await Read<UpsertResultDto>(resp1);
        r1.Data!.Failed.Should().Be(0);
        r1.Data!.Inserted.Should().Be(1);
        (await ResolvedSupplierIdAsync(po1)).Should().Be(supA.SupplierId, because: "erpSupplierCode matched Supplier.ErpCode");

        // ---- Flow 3: BOTH present → erpSupplierCode WINS (binds A), supplierCode (B) is ignored. ----
        var po3 = $"PO-ERP3-{tag}";
        var resp3 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po3, supplierCode: supB.SupplierCode, erpSupplierCode: erp));
        resp3.StatusCode.Should().Be(HttpStatusCode.OK);
        var r3 = await Read<UpsertResultDto>(resp3);
        r3.Data!.Failed.Should().Be(0);
        r3.Data!.Inserted.Should().Be(1);
        (await ResolvedSupplierIdAsync(po3)).Should().Be(supA.SupplierId,
            because: "with both identifiers present, erpSupplierCode takes priority over supplierCode");

        // ---- Unknown erpCode → per-row failure (200, Failed=1, nothing inserted). ----
        var po9 = $"PO-ERP9-{tag}";
        var resp9 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po9, supplierCode: null, erpSupplierCode: $"NOPE-{tag}"));
        resp9.StatusCode.Should().Be(HttpStatusCode.OK);
        var r9 = await Read<UpsertResultDto>(resp9);
        r9.Data!.Inserted.Should().Be(0);
        r9.Data!.Failed.Should().Be(1, because: "an unresolvable erpSupplierCode fails that row, not the whole batch");
        (await ResolvedSupplierIdAsync(po9)).Should().BeNull();

        // ---- Flow 4: NEITHER identifier → request rejected by the validator (400), nothing persists. ----
        var po4 = $"PO-ERP4-{tag}";
        var resp4 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po4, supplierCode: null, erpSupplierCode: null));
        resp4.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ResolvedSupplierIdAsync(po4)).Should().BeNull();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
