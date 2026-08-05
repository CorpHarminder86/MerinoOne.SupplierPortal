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

        // PO B's LINE is force-nulled after ingest to simulate a line ingested before R13 (warehouse is per line
        // now and the inbound contract requires it, so a null warehouse is only reachable for legacy rows). Null the
        // code AND the resolved address FK + snapshot so the resolver treats the line as warehouse-absent.
        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCodeAlt);
        using (var seed = _fx.Factory.Services.CreateScope())
        {
            var sdb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var lineB = await sdb.PurchaseOrderLines.IgnoreQueryFilters().FirstAsync(l => l.Id == ctx.LineB);
            lineB.Warehouse = null;
            lineB.WarehouseAddressId = null;
            lineB.WarehouseAddressSnapshot = null;
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

    // ── R11 (D6/D7/D8) — mandatory shipment references at Send-For-Approval ─────────────────────────────

    [SkippableTheory] // Each ref is independently required, and the error names the missing one(s).
    [InlineData(null, "BOL-1", "PL-1", "invoice no.")]
    [InlineData("INV-1", null, "PL-1", "bill of lading")]
    [InlineData("INV-1", "BOL-1", null, "packing list")]
    [InlineData("   ", "BOL-1", "PL-1", "invoice no.")]   // whitespace counts as absent
    public async Task Send_for_approval_is_blocked_until_all_three_shipment_refs_are_set(
        string? invoiceNo, string? bol, string? packingList, string expectedInMessage)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, client) = await DraftWithRefsAsync(invoiceNo, bol, packingList);

        var send = await client.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "all three shipment references are mandatory before an ASN leaves Draft");
        (await send.Content.ReadAsStringAsync()).Should().Contain(expectedInMessage);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId)).AsnStatus
            .Should().Be(AsnStatus.Draft, because: "a blocked send must not move the ASN off Draft");
    }

    [SkippableFact] // The refs are optional at CREATE (D7) — a partial draft can still be parked.
    public async Task Draft_can_be_created_and_saved_with_blank_shipment_refs()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _) = await DraftWithRefsAsync(null, null, null);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
        asn.AsnStatus.Should().Be(AsnStatus.Draft);
        asn.InvoiceNo.Should().BeNull(because: "creation does not require the refs — only Send-For-Approval does");
    }

    [SkippableFact] // With all three set, the send proceeds.
    public async Task Send_for_approval_succeeds_once_all_three_refs_are_set()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, client) = await DraftWithRefsAsync("INV-9", "BOL-9", "PL-9");

        var send = await client.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await send.Content.ReadAsStringAsync());

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId)).AsnStatus
            .Should().Be(AsnStatus.PendingApproval);
    }

    /// <summary>Creates a single-PO draft ASN carrying the given shipment refs, and returns it with a supplier client.</summary>
    private async Task<(Guid AsnId, HttpClient Client)> DraftWithRefsAsync(string? invoiceNo, string? bol, string? packingList)
    {
        var ctx = await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCode);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, ctx.PoIdA);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var req = AsnOver(ctx.LineA, poA: ctx.PoIdA) with
        {
            InvoiceNo = invoiceNo,
            BillOfLading = bol,
            PackingList = packingList,
        };

        var create = await client.PostAsJsonAsync("/api/asns", req);
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await create.Content.ReadAsStringAsync());
        return ((await Read<AsnDetailDto>(create)).Data!.Id, client);
    }

    // ── R11.1 — serial/lot uniqueness, per item within the company ──────────────────────────────────────

    [SkippableFact] // THE E2E REGRESSION: same serial on two lines of ONE ASN, drawn from two DIFFERENT POs.
    public async Task Same_serial_on_two_lines_from_different_pos_is_rejected_at_create()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupSerializedAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Both lines are the SAME item, so serial 2222 identifies one physical unit — it cannot be on both.
        // Before R11.1 this passed: the cross-ASN check was PO-scoped and the intra-ASN check was per-LINE.
        var req = new CreateAsnRequest(
            PurchaseOrderId: null,
            PurchaseOrderIds: new[] { ctx.PoIdA, ctx.PoIdB },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.LineA, ShippedQty: 1, BatchNumber: null, ExpiryDate: null,
                    Serials: new List<AsnLineSerialInput> { new("2222") }),
                new(ctx.LineB, ShippedQty: 1, BatchNumber: null, ExpiryDate: null,
                    Serials: new List<AsnLineSerialInput> { new("2222") }),
            });

        var resp = await client.PostAsJsonAsync("/api/asns", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "one physical unit cannot ship twice, even across two POs");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("captured more than once");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().AnyAsync(a => a.SupplierId == ctx.SupplierId))
            .Should().BeFalse(because: "the rejected ASN must not be partially persisted");
    }

    [SkippableFact] // A serial already held by ANOTHER live ASN is rejected at create, not at submit.
    public async Task Serial_already_used_on_another_asn_is_rejected_at_create()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupSerializedAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var first = await client.PostAsJsonAsync("/api/asns", OneSerialAsn(ctx.PoIdA, ctx.LineA, "SN-DUP"));
        first.StatusCode.Should().Be(HttpStatusCode.OK, because: await first.Content.ReadAsStringAsync());

        // A DRAFT reserves its serials, so the second ASN cannot claim the same unit — even on a different PO.
        var second = await client.PostAsJsonAsync("/api/asns", OneSerialAsn(ctx.PoIdB, ctx.LineB, "SN-DUP"));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a draft reserves its serials; the unit is already spoken for");
        (await second.Content.ReadAsStringAsync()).Should().Contain("already used");
    }

    [SkippableFact] // Cancelling an ASN RELEASES its serials for reuse.
    public async Task Cancelled_asn_releases_its_serials()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupSerializedAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var first = await client.PostAsJsonAsync("/api/asns", OneSerialAsn(ctx.PoIdA, ctx.LineA, "SN-REL"));
        first.StatusCode.Should().Be(HttpStatusCode.OK, because: await first.Content.ReadAsStringAsync());
        var firstId = (await Read<AsnDetailDto>(first)).Data!.Id;

        var cancel = await client.PostAsJsonAsync($"/api/asns/{firstId}/cancel", new { });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, because: await cancel.Content.ReadAsStringAsync());

        var second = await client.PostAsJsonAsync("/api/asns", OneSerialAsn(ctx.PoIdB, ctx.LineB, "SN-REL"));
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a cancelled ASN releases its serials — otherwise a typo would burn a unit forever");
    }

    [SkippableFact] // Re-saving a draft without changing its capture must not clash with ITSELF.
    public async Task Updating_a_draft_does_not_clash_with_its_own_serials()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupSerializedAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await client.PostAsJsonAsync("/api/asns", OneSerialAsn(ctx.PoIdA, ctx.LineA, "SN-SELF"));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await create.Content.ReadAsStringAsync());
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var update = new UpdateAsnRequest(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(3),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.LineA, ShippedQty: 1, BatchNumber: null, ExpiryDate: null,
                    Serials: new List<AsnLineSerialInput> { new("SN-SELF") }),
            });

        var resp = await client.PutAsJsonAsync($"/api/asns/{asnId}", update);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the ASN being edited is excluded from its own uniqueness check");
    }

    private static CreateAsnRequest OneSerialAsn(Guid poId, Guid lineId, string serial) => new(
        PurchaseOrderId: poId,
        PurchaseOrderIds: null,
        ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
        TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
        DriverName: null, DriverPhone: null, Notes: null,
        Lines: new List<CreateAsnLineRequest>
        {
            new(lineId, ShippedQty: 1, BatchNumber: null, ExpiryDate: null,
                Serials: new List<AsnLineSerialInput> { new(serial) }),
        });

    /// <summary>Two POs in the SAME warehouse (so the D4 invariant is not what fails), one serialized item.</summary>
    private async Task<Ctx> SetupSerializedAsync() =>
        await SetupAsync(IntegrationTestFixture.WarehouseCode, IntegrationTestFixture.WarehouseCode, serialized: true);

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────

    private record Ctx(Guid SupplierId, Guid PoIdA, Guid PoIdB, Guid LineA, Guid LineB);

    /// <summary>Pushes two single-line POs for one fresh supplier, each in the given warehouse, and confirms both
    /// (a Released PO under the default AcceptToShip mode blocks ASN creation).</summary>
    private async Task<Ctx> SetupAsync(string warehouseA, string warehouseB, bool serialized = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var item = await _fx.CreateItemAsync($"WH-ITM-{tag}", isSerialized: serialized);

        var poA = $"PO-WHA-{tag}";
        var poB = $"PO-WHB-{tag}";

        // R13 — warehouse is per LINE now (header ship-to/warehouse retired). Each PO's single line carries the
        // requested warehouse code, which hard-resolves to a CompanyAddress; the ASN single-warehouse invariant
        // then fires on the covered lines' warehouse addresses.
        PoRecord Po(string poNumber, string warehouse) => new(
            PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
            Lines: new[]
            {
                new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: item.ItemCode,
                    OrderUnit: "EA", OrderQty: 50, PriceUnit: 1, Price: 50, Warehouse: warehouse),
            },
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
