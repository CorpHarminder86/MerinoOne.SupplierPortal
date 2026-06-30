using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 (TSD R5 Addendum §8 / Component 4) — Delivery Schedule creation triggers + grid, through the REAL host.
/// Schedules are PORTAL-CREATED when a PO becomes shippable (§8.1) and upserted in place on a material Modify
/// (§8.2); the single shared <c>DeliveryScheduleFactory</c> is the dedup point for every trigger.
///
/// <list type="bullet">
///   <item>UC-DS-01 — schedules created on Accept (AcceptToShip mode).</item>
///   <item>UC-DS-02 — schedules created for AutoAccept at Released (ingest auto-stamp).</item>
///   <item>UC-DS-03 — material Modify upserts in place, NO duplicate (one row per line).</item>
///   <item>UC-DS-04 — grid sort PO → Line → DeliveryDate ASC + line detail.</item>
///   <item>UC-DS-05 — grid filters + ship-to auto-hide signal.</item>
/// </list>
///
/// <para>Runs with the scope gate OFF (money path). Each test owns a fresh tagged supplier + PO via the inbound
/// push; the PO transition is driven through the real supplier endpoints so the whole command chain (and the
/// schedule wiring) is exercised. DB assertions go through a fresh scope.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DeliveryScheduleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public DeliveryScheduleTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-DS-01 — schedules created on Accept (AcceptToShip) ────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_DS_01_Schedules_created_on_Accept()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // AcceptToShip default — a Released PO has NO schedules until the supplier accepts.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        (await ScheduleCountAsync(setup.PoId)).Should().Be(0, because: "no schedule exists before the PO is shippable (§8.1)");

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var acceptResp = await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/accept", new AcceptPoRequest());
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(acceptResp));

        // One DeliverySchedule per PO line (the seeded PO has one line), Approved, carrying the line's date/qty + PO ship-to.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schedules = await db.DeliverySchedules.IgnoreQueryFilters()
            .Where(s => s.PurchaseOrderId == setup.PoId && !s.IsDeleted).ToListAsync();
        schedules.Should().HaveCount(1, because: "one schedule per non-deleted PO line is created on Accepted (UC-DS-01)");
        var sch = schedules.Single();
        sch.PurchaseOrderLineId.Should().Be(setup.PoLineId);
        sch.Status.Should().Be(DeliveryScheduleStatus.Approved);
        sch.ScheduledQty.Should().Be(setup.OrderQty, because: "scheduledQty = line.orderQty at creation");
        sch.ShipToAddressId.Should().Be(IntegrationTestFixture.ShipToAddressId, because: "shipToAddressId = PO.shipToAddressId");
        sch.SeccodeId.Should().Be(setup.Supplier.SeccodeId, because: "the schedule inherits the owning PO's G-seccode for RLS");
    }

    // ── UC-DS-02 — schedules created for AutoAccept at Released (ingest auto-stamp) ───────────────────────
    [SkippableFact]
    public async Task UC_DS_02_Schedules_created_for_AutoAccept_at_Released()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        // AutoAccept BEFORE the push — the ingest auto-stamps Released → Accepted and creates schedules immediately.
        await SetConfirmationModeAsync(supplier.SupplierId, PoConfirmationMode.AutoAccept);

        var poNumber = $"PO-DSAUTO-{tag}";
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[]
                {
                    new PoLineRecord(10, 1, $"ITM-A-{tag}", OrderUnit: "EA", OrderQty: 30, PriceUnit: 1, Price: 30),
                    new PoLineRecord(20, 2, $"ITM-B-{tag}", OrderUnit: "EA", OrderQty: 40, PriceUnit: 1, Price: 40),
                },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.PoNumber == poNumber);
        po.PoStatus.Should().Be(PoStatus.Accepted, because: "AutoAccept auto-stamps at ingest");
        var schedules = await db.DeliverySchedules.IgnoreQueryFilters()
            .Where(s => s.PurchaseOrderId == po.Id && !s.IsDeleted).ToListAsync();
        schedules.Should().HaveCount(2, because: "one schedule per PO line is created immediately for AutoAccept at Released (UC-DS-02)");
        schedules.Select(s => s.ScheduledQty).Should().BeEquivalentTo(new[] { 30m, 40m });
        schedules.Should().OnlyContain(s => s.ShipToAddressId == IntegrationTestFixture.ShipToAddressId);
        schedules.Should().OnlyContain(s => s.Status == DeliveryScheduleStatus.Approved);
    }

    // ── UC-DS-03 — material Modify upserts in place, NO duplicate ─────────────────────────────────────────
    [SkippableFact]
    public async Task UC_DS_03_Material_modify_upserts_in_place_no_duplicate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Accept → one schedule created (scheduledQty = original orderQty 10).
        (await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/accept", new AcceptPoRequest()))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Guid scheduleId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sch = await db.DeliverySchedules.IgnoreQueryFilters().SingleAsync(s => s.PurchaseOrderId == setup.PoId && !s.IsDeleted);
            sch.ScheduledQty.Should().Be(setup.OrderQty);
            scheduleId = sch.Id;
        }

        // ERP MATERIAL modify (orderQty 10 → 25) re-arms confirmation (PO → Released) and refreshes the existing
        // schedule in place (refreshOnly path); then the supplier RE-ACCEPTS, which runs the create-or-refresh path.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(setup.PoNumber, setup.Supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(setup.PoPositionNo, 1, setup.ItemCode, OrderUnit: "EA", OrderQty: 25, PriceUnit: setup.PriceUnit, Price: setup.PriceUnit * 25) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));
        await AssertPoStatus(setup.PoId, PoStatus.Released);   // material modify re-armed the gate

        (await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/accept", new AcceptPoRequest()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // STILL exactly one schedule for the line (SAME id — upserted in place, never duplicated) carrying the new qty.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedules = await db.DeliverySchedules.IgnoreQueryFilters()
                .Where(s => s.PurchaseOrderLineId == setup.PoLineId && !s.IsDeleted).ToListAsync();
            schedules.Should().HaveCount(1, because: "the material Modify upserts the schedule in place — no duplicate row (UC-DS-03)");
            schedules.Single().Id.Should().Be(scheduleId, because: "the existing schedule keeps its id (refreshed, not recreated)");
            schedules.Single().ScheduledQty.Should().Be(25m, because: "the schedule's qty was refreshed to the revised orderQty");
        }
    }

    // ── UC-DS-04 — grid sort PO → Line → DeliveryDate ASC + line detail ───────────────────────────────────
    [SkippableFact]
    public async Task UC_DS_04_Grid_sorts_and_carries_line_detail()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        await SetConfirmationModeAsync(supplier.SupplierId, PoConfirmationMode.AutoAccept);

        // Two POs (number ordering: B then A pushed so we prove the grid sorts by PO number, not insert order), each
        // two lines with different delivery dates so the Line + DeliveryDate ordering is observable.
        var poA = $"PO-DSA-{tag}";
        var poB = $"PO-DSB-{tag}";
        var d1 = DateTime.UtcNow.Date.AddDays(5);
        var d2 = DateTime.UtcNow.Date.AddDays(10);
        await PushAsync(PoBody(poB, supplier.SupplierCode, tag, (20, 5m, d2), (10, 7m, d1)));
        await PushAsync(PoBody(poA, supplier.SupplierCode, tag, (10, 3m, d1), (20, 9m, d2)));

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var grid = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync($"/api/delivery-schedules?supplierId={supplier.SupplierId}&pageSize=200"))).Data!;

        var rows = grid.Page.Items;
        rows.Should().HaveCount(4, because: "two POs × two lines = four schedules");

        // Sort: PO number ASC, then PositionNo ASC, then DeliveryDate ASC. With one schedule per line the position
        // ordering dominates within a PO; assert the PO→Line key sequence.
        var keySequence = rows.Select(r => (r.PoNumber, r.PositionNo)).ToList();
        keySequence.Should().Equal(
            (poA, 10), (poA, 20), (poB, 10), (poB, 20));

        // Line detail surfaced + RemainingToShip derived from the R4 balance (no shipments yet → orderQty).
        var first = rows[0];
        first.PoNumber.Should().Be(poA);
        first.PositionNo.Should().Be(10);
        first.ItemCode.Should().Be($"ITM-{tag}-10");
        first.OrderQty.Should().Be(3m);
        first.RemainingToShip.Should().Be(3m, because: "remaining = MAX(0, orderQty − shippedQtyToDate) with nothing shipped");
        first.ShipToAddressName.Should().Be("IntTest DC");
        first.DeliveryDate.Date.Should().Be(d1);
    }

    // ── UC-DS-05 — grid filters + ship-to auto-hide signal ───────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_DS_05_Grid_filters_and_shipTo_autohide()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        await SetConfirmationModeAsync(supplier.SupplierId, PoConfirmationMode.AutoAccept);

        var poX = $"PO-DSX-{tag}";
        var poY = $"PO-DSY-{tag}";
        var dEarly = DateTime.UtcNow.Date.AddDays(3);
        var dLate = DateTime.UtcNow.Date.AddDays(20);
        await PushAsync(PoBody(poX, supplier.SupplierCode, tag, (10, 4m, dEarly)));
        await PushAsync(PoBody(poY, supplier.SupplierCode, tag, (10, 6m, dLate)));

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // (a) PO filter narrows to one PO's schedules.
        var byPo = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync($"/api/delivery-schedules?supplierId={supplier.SupplierId}&purchaseOrderId={await PoIdAsync(poX)}&pageSize=200"))).Data!;
        byPo.Page.Items.Should().OnlyContain(r => r.PoNumber == poX, because: "the PO filter restricts to one PO");

        // (b) Delivery-date range filter narrows to the early schedule only.
        var byDate = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync(
                $"/api/delivery-schedules?supplierId={supplier.SupplierId}&deliveryDateFrom={dEarly:yyyy-MM-dd}&deliveryDateTo={dEarly:yyyy-MM-dd}&pageSize=200"))).Data!;
        byDate.Page.Items.Should().OnlyContain(r => r.PoNumber == poX,
            because: "only the early-dated schedule falls inside the inclusive single-day range");

        // (c) Status filter (Approved) returns the rows; a non-matching status returns none.
        var byStatus = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync($"/api/delivery-schedules?supplierId={supplier.SupplierId}&status=Approved&pageSize=200"))).Data!;
        byStatus.Page.Items.Should().HaveCount(2, because: "both schedules are Approved");

        // (d) Ship-to auto-hide: this supplier's schedules all share ONE ship-to → the filter is hidden.
        var grid = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync($"/api/delivery-schedules?supplierId={supplier.SupplierId}&pageSize=200"))).Data!;
        grid.DistinctShipToCount.Should().Be(1, because: "all of this supplier's schedules share one ship-to");
        grid.ShowShipToFilter.Should().BeFalse(because: "the Ship-To filter auto-hides when only one ship-to is present (§7, UC-DS-05)");

        // (e) Ship-to filter still selects the matching rows when supplied explicitly.
        var byShipTo = (await Read<DeliveryScheduleGridDto>(
            await supplierClient.GetAsync(
                $"/api/delivery-schedules?supplierId={supplier.SupplierId}&shipToAddressId={IntegrationTestFixture.ShipToAddressId}&pageSize=200"))).Data!;
        byShipTo.Page.Items.Should().HaveCount(2, because: "both schedules carry the one seeded ship-to");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────
    private static PushPurchaseOrdersRequest PoBody(
        string poNumber, string supplierCode, string tag, params (int Position, decimal Qty, DateTime DeliveryDate)[] lines)
        => new(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplierCode, DateTime.UtcNow.Date,
                lines.Select(l => new PoLineRecord(
                    PositionNo: l.Position, SequenceNo: 1, ItemCode: $"ITM-{tag}-{l.Position}",
                    OrderUnit: "EA", OrderQty: l.Qty, PriceUnit: 1, Price: l.Qty, DeliveryDate: l.DeliveryDate)).ToArray(),
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });

    private async Task<Guid> PoIdAsync(string poNumber)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.PoNumber == poNumber).Select(p => p.Id).FirstAsync();
    }

    private async Task<int> ScheduleCountAsync(Guid poId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeliverySchedules.IgnoreQueryFilters().CountAsync(s => s.PurchaseOrderId == poId && !s.IsDeleted);
    }

    private async Task SetConfirmationModeAsync(Guid supplierId, PoConfirmationMode mode)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var supplier = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == supplierId);
        supplier.PoConfirmationMode = mode;
        await db.SaveChangesAsync();
    }

    private async Task AssertPoStatus(Guid poId, PoStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId).Select(p => p.PoStatus).FirstAsync())
            .Should().Be(expected);
    }

    private async Task PushAsync(PushPurchaseOrdersRequest body)
    {
        var client = _fx.CreateInboundClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var resp = await client.PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Read<UpsertResultDto>(resp);
        r.Data!.Failed.Should().Be(0,
            because: r.Data.Rows.Count > 0 ? string.Join(" | ", r.Data.Rows.Select(x => $"{x.Code}/{x.Outcome}/{x.Error}")) : "no failures expected");
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
