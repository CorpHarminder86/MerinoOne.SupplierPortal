using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R11 (D4) — the ASN single-warehouse invariant, through the REAL host.
/// <para>Warehouse is a grouping key exactly like ship-to: LN's whinh.advanceShipmentNotices carries ONE
/// receiving warehouse, so an ASN spanning two would be misrouted at the ERP. These tests cover the happy
/// snapshot, the cross-warehouse rejection, the null-tolerance rule for pre-R11 POs, and re-derivation on
/// update (where a supplier can swap the line set out from under the stored value).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnWarehouseTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IntegrationTestFixture _fx;

    public AsnWarehouseTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact] // Happy path — a single-warehouse ASN snapshots the covered PO's warehouse onto the header.
    public async Task Asn_snapshots_warehouse_from_its_purchase_order()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCode);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var resp = await client.PostAsJsonAsync("/api/asns", AsnOver(ctx.LineA, poA: ctx.PoIdA));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());

        var asnId = (await Read<AsnDetailDto>(resp)).Data!.Id;
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
        asn.Warehouse.Should().Be(IntegrationTestFixture.WarehouseCode,
            because: "the ASN snapshots the warehouse off the PO it ships against");
    }

    [SkippableFact] // The invariant — lines drawn from POs in two warehouses are rejected, naming both codes.
    public async Task Asn_spanning_two_warehouses_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCodeAlt);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var resp = await client.PostAsJsonAsync("/api/asns", AsnOver(ctx.LineA, ctx.LineB, ctx.PoIdA, ctx.PoIdB));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "an ASN may not mix receiving warehouses");

        var payload = await resp.Content.ReadAsStringAsync();
        payload.Should().Contain("cannot mix warehouses");
        payload.Should().Contain(IntegrationTestFixture.WarehouseCode, because: "the error names the conflicting codes");
        payload.Should().Contain(IntegrationTestFixture.WarehouseCodeAlt);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().AnyAsync(a => a.SupplierId == ctx.SupplierId))
            .Should().BeFalse(because: "a rejected ASN must not be partially persisted");
    }

    [SkippableFact] // Pre-R11 tolerance — a null warehouse is NOT a distinct value, so a mixed null/set selection passes.
    public async Task Asn_treats_null_warehouse_as_absent_not_as_a_distinct_group()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // PO B is force-nulled after ingest to simulate a PO ingested before R11 (the inbound contract now
        // requires a warehouse, so this state is only reachable for legacy rows).
        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCodeAlt);
        using (var seed = _fx.Factory.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var poB = await sdb.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == ctx.PoIdB);
            poB.Warehouse = null;
            await sdb.SaveChangesAsync();
        }

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await client.PostAsJsonAsync("/api/asns", AsnOver(ctx.LineA, ctx.LineB, ctx.PoIdA, ctx.PoIdB));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a legacy null warehouse must not block an ASN that is otherwise single-warehouse");

        var asnId = (await Read<AsnDetailDto>(resp)).Data!.Id;
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId)).Warehouse
            .Should().Be(IntegrationTestFixture.WarehouseCode,
                because: "the one non-null warehouse in the selection wins");
    }

    [SkippableFact] // Update re-derives — swapping in a line from another warehouse is rejected on PUT, not just POST.
    public async Task Updating_an_asn_to_span_two_warehouses_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCodeAlt);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var created = await client.PostAsJsonAsync("/api/asns", AsnOver(ctx.LineA, poA: ctx.PoIdA));
        created.StatusCode.Should().Be(HttpStatusCode.OK, because: await created.Content.ReadAsStringAsync());
        var asnId = (await Read<AsnDetailDto>(created)).Data!.Id;

        // Add PO B's line (a different warehouse) to the existing draft.
        var update = new UpdateAsnRequest(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.LineA, ShippedQty: 1, BatchNumber: null, ExpiryDate: null),
                new(ctx.LineB, ShippedQty: 1, BatchNumber: null, ExpiryDate: null),
            });

        var resp = await client.PutAsJsonAsync($"/api/asns/{asnId}", update);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the update path re-derives the warehouse from the NEW line set");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("cannot mix warehouses");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────

    private record Ctx(Guid SupplierId, Guid PoIdA, Guid PoIdB, Guid LineA, Guid LineB);

    /// <summary>Pushes two single-line POs for one fresh supplier, each in the given warehouse, and confirms both
    /// (a Released PO under the default AcceptToShip mode blocks ASN creation).</summary>
    private async Task<Ctx> SetupAsync(string warehouseA, string warehouseB)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var item = await _fx.CreateItemAsync($"WH-ITM-{tag}");

        var poA = $"PO-WHA-{tag}";
        var poB = $"PO-WHB-{tag}";

        PoRecord Po(string poNumber, string warehouse) => new(
            PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
            Lines: new[]
            {
                new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: item.ItemCode,
                    OrderUnit: "EA", OrderQty: 50, PriceUnit: 1, Price: 50),
            },
            ShipToAddress: IntegrationTestFixture.ShipToErpCode,
            Warehouse: warehouse,
            PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR");

        var resp = await _fx.CreateInboundClient().PostAsJsonAsync("/api/integration/inbound/purchase-orders",
            new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode,
                new[] { Po(poA, warehouseA), Po(poB, warehouseB) }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pos = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .Where(p => (p.PoNumber == poA || p.PoNumber == poB) && p.TenantId == IntegrationTestFixture.TenantId)
            .ToListAsync();
        var a = pos.Single(p => p.PoNumber == poA);
        var b = pos.Single(p => p.PoNumber == poB);

        foreach (var po in pos) { po.PoStatus = PoStatus.Accepted; po.AcceptedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();

        return new Ctx(supplier.SupplierId, a.Id, b.Id,
            a.Lines.Single(l => l.PositionNo == 10).Id,
            b.Lines.Single(l => l.PositionNo == 10).Id);
    }

    private static CreateAsnRequest AsnOver(Guid lineA, Guid? lineB = null, Guid? poA = null, Guid? poB = null)
    {
        var lines = new List<CreateAsnLineRequest>
        {
            new(lineA, ShippedQty: 1, BatchNumber: null, ExpiryDate: null),
        };
        if (lineB is { } lb) lines.Add(new(lb, ShippedQty: 1, BatchNumber: null, ExpiryDate: null));

        return new CreateAsnRequest(
            PurchaseOrderId: lineB is null ? poA : null,
            PurchaseOrderIds: lineB is null ? null : new[] { poA!.Value, poB!.Value },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: lines);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
