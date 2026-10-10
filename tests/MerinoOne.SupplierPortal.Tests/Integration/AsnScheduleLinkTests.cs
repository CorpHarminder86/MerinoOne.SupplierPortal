using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R15 — ASN line → delivery-schedule back-links on the generic create/update paths. The schedule-driven wizard
/// sends an optional <c>deliveryScheduleId</c> per line; it must persist on create, SURVIVE the update replace-set
/// (before R15 every draft save wiped it), surface on the detail DTO (id + schedule delivery date), and reject a
/// stale/foreign/duplicated id with a 400 rather than silently mislinking.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnScheduleLinkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnScheduleLinkTests(IntegrationTestFixture fx) => _fx = fx;

    // ── create persists the link + the detail DTO surfaces it, and update PRESERVES it ────────────────────
    [SkippableFact]
    public async Task Create_persists_schedule_link_and_update_preserves_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Accept through the REAL endpoint so the §8.1 trigger materialises the line's Approved schedule
        // (SeedPoAsync's confirm:true stamps the status directly and bypasses the schedule factory).
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        await AcceptAsync(client, setup.PoId);
        var (scheduleId, scheduleDate) = await ScheduleOfLineAsync(setup.PoLineId);
        var createResp = await client.PostAsJsonAsync("/api/asns", Req(setup.PoId, new List<CreateAsnLineRequest>
        {
            new(setup.PoLineId, ShippedQty: 4, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: scheduleId),
        }));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var created = (await Read<AsnDetailDto>(createResp)).Data!;

        created.Lines.Should().HaveCount(1);
        created.Lines[0].DeliveryScheduleId.Should().Be(scheduleId, because: "the create path persists the validated back-link (R15)");
        created.Lines[0].ScheduleDeliveryDate.Should().Be(scheduleDate, because: "the DTO joins the schedule's delivery date for the wizard");

        // Draft save (full line replace) keeps the link — the R15 regression this suite exists for.
        var updResp = await client.PutAsJsonAsync($"/api/asns/{created.Id}", new UpdateAsnRequest(
            DateTime.UtcNow.Date.AddDays(3), null, "Carrier X", null, null, null, null, null,
            new List<CreateAsnLineRequest>
            {
                new(setup.PoLineId, ShippedQty: 6, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: scheduleId),
            }));
        updResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(updResp));
        var updated = (await Read<AsnDetailDto>(updResp)).Data!;
        updated.Lines.Should().HaveCount(1);
        updated.Lines[0].ShippedQty.Should().Be(6);
        updated.Lines[0].DeliveryScheduleId.Should().Be(scheduleId, because: "the update replace-set carries the back-link through (R15)");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedLinks = await db.AsnLines.IgnoreQueryFilters()
            .Where(l => l.AsnId == created.Id && !l.IsDeleted)
            .Select(l => l.DeliveryScheduleId)
            .ToListAsync();
        storedLinks.Should().Equal(new Guid?[] { scheduleId });
    }

    // ── a schedule id that is not a live schedule of the SAME PO line → 400, never a silent mislink ───────
    [SkippableFact]
    public async Task Create_rejects_schedule_of_a_different_line()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var other = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);   // a second supplier's PO line + schedule
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        await AcceptAsync(client, setup.PoId);
        await AcceptAsync(client, other.PoId);
        var (foreignScheduleId, _) = await ScheduleOfLineAsync(other.PoLineId);
        var resp = await client.PostAsJsonAsync("/api/asns", Req(setup.PoId, new List<CreateAsnLineRequest>
        {
            // Line is legit, but the back-link points at ANOTHER PO line's schedule.
            new(setup.PoLineId, ShippedQty: 1, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: foreignScheduleId),
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a back-link must reference a live schedule of the SAME PO line: " + await Body(resp));

        // Unknown id → same 400 (not-found is a validation reject on this path, not a 404).
        var respUnknown = await client.PostAsJsonAsync("/api/asns", Req(setup.PoId, new List<CreateAsnLineRequest>
        {
            new(setup.PoLineId, ShippedQty: 1, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: Guid.NewGuid()),
        }));
        respUnknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(respUnknown));
    }

    // ── the same schedule on two request lines → 400 (validator) ──────────────────────────────────────────
    [SkippableFact]
    public async Task Create_rejects_duplicate_schedule_links()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        await AcceptAsync(client, setup.PoId);
        var (scheduleId, _) = await ScheduleOfLineAsync(setup.PoLineId);

        var resp = await client.PostAsJsonAsync("/api/asns", Req(setup.PoId, new List<CreateAsnLineRequest>
        {
            new(setup.PoLineId, ShippedQty: 2, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: scheduleId),
            new(setup.PoLineId, ShippedQty: 3, BatchNumber: null, ExpiryDate: null, DeliveryScheduleId: scheduleId),
        }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "each delivery schedule may appear on at most one ASN line: " + await Body(resp));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // 2026-10-10 — ship-quantity PRECISION on POST /api/asns/from-schedule. The schedule-driven wizard (/asns/new)
    // creates through this endpoint with client-typed quantities (ShipQtyByScheduleId), so the same decimal-places rule
    // Create/Update enforce (mdm.Unit.decimalPlaces of the PO line's unit; unknown → 3) must hold here — for a typed
    // quantity AND for the defaulted line balance — before anything is written. The 400 body carries messages only, so
    // the message names the PO and line position. Units are the UT-ASNQ-* codes AsnQuantityTests also uses (idempotent).
    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    private const string ZeroDecimalsUnit = "UT-ASNQ-0";
    private const string TwoDecimalsUnit = "UT-ASNQ-2";

    private static string ScaleMessage(ProcureToPayFlow.Setup po, string unit, int places)
        => $"Ship quantity on {po.PoNumber} line {po.PoPositionNo} for unit {unit} allows at most {places} decimal places.";

    // excess decimals → 400 naming the PO line + unit; nothing persisted; the same request with a whole quantity is created.
    [SkippableFact]
    public async Task FromSchedule_rejects_a_typed_quantity_with_more_decimals_than_the_unit_allows()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, scheduleId) = await ScheduledPoWithUnitAsync(ZeroDecimalsUnit, decimals: 0);

        var bad = await client.PostAsJsonAsync("/api/asns/from-schedule",
            FromSchedule(new[] { scheduleId }, new Dictionary<Guid, decimal> { [scheduleId] = 2.5m }));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(bad));
        (await Read<AsnDetailDto>(bad)).Errors.Should().Equal(
            new[] { ScaleMessage(po, ZeroDecimalsUnit, 0) },
            because: "one message per offending line, naming its PO and line position, the unit and the precision it allows");
        (await AsnLineCountAsync(po)).Should().Be(0, because: "a rejected create persists no ASN header or line");

        // The very same schedule with a whole quantity (however spelled) goes through.
        var ok = await client.PostAsJsonAsync("/api/asns/from-schedule",
            FromSchedule(new[] { scheduleId }, new Dictionary<Guid, decimal> { [scheduleId] = 4.0m }));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));
        var line = (await Read<AsnDetailDto>(ok)).Data!.Lines.Single();
        line.ShippedQty.Should().Be(4m);
        line.UnitDecimalPlaces.Should().Be(0);
        line.DeliveryScheduleId.Should().Be(scheduleId);
    }

    // exactly n decimals → created; n + 1 → 400.
    [SkippableFact]
    public async Task FromSchedule_accepts_exactly_the_unit_decimals_and_rejects_one_more()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, scheduleId) = await ScheduledPoWithUnitAsync(TwoDecimalsUnit, decimals: 2);

        var tooMany = await client.PostAsJsonAsync("/api/asns/from-schedule",
            FromSchedule(new[] { scheduleId }, new Dictionary<Guid, decimal> { [scheduleId] = 1.255m }));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(tooMany));
        (await Read<AsnDetailDto>(tooMany)).Errors.Should().Equal(new[] { ScaleMessage(po, TwoDecimalsUnit, 2) });
        (await AsnLineCountAsync(po)).Should().Be(0);

        var exact = await client.PostAsJsonAsync("/api/asns/from-schedule",
            FromSchedule(new[] { scheduleId }, new Dictionary<Guid, decimal> { [scheduleId] = 1.25m }));
        exact.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(exact));
        var asn = (await Read<AsnDetailDto>(exact)).Data!;
        asn.AsnStatus.Should().Be("Draft");
        var line = asn.Lines.Single();
        line.ShippedQty.Should().Be(1.25m);
        line.UnitDecimalPlaces.Should().Be(2);
    }

    // The wizard leaves untouched rows to default to the line balance; that default lands on the ASN line too, so it is
    // judged like a typed one (an ERP-fed order quantity is not guaranteed to fit the unit master).
    [SkippableFact]
    public async Task FromSchedule_judges_the_defaulted_balance_like_a_typed_quantity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, scheduleId) = await ScheduledPoWithUnitAsync(ZeroDecimalsUnit, decimals: 0);
        await SetPoLineAsync(po.PoLineId, orderQty: 10.5m);   // balance = 10.5 on a unit that allows no decimals

        var defaulted = await client.PostAsJsonAsync("/api/asns/from-schedule", FromSchedule(new[] { scheduleId }));
        defaulted.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(defaulted));
        (await Read<AsnDetailDto>(defaulted)).Errors.Should().Equal(new[] { ScaleMessage(po, ZeroDecimalsUnit, 0) });
        (await AsnLineCountAsync(po)).Should().Be(0);

        // A typed, valid quantity for the same schedule overrides the unusable default.
        var typed = await client.PostAsJsonAsync("/api/asns/from-schedule",
            FromSchedule(new[] { scheduleId }, new Dictionary<Guid, decimal> { [scheduleId] = 10m }));
        typed.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(typed));
        (await Read<AsnDetailDto>(typed)).Data!.Lines.Single().ShippedQty.Should().Be(10m);
    }

    // Two POs on one ASN: only the offending line is reported, and the message says WHICH PO it is.
    [SkippableFact]
    public async Task FromSchedule_names_the_offending_po_when_several_pos_are_selected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await UnitMasterDecimalsTests.EnsureUnitAsync(_fx, ZeroDecimalsUnit, 0);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var poA = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 10m);
        var poB = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 20m);
        var schedA = (await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, client, poA.PoId))[poA.PoLineId];
        var schedB = (await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, client, poB.PoId))[poB.PoLineId];
        await SetPoLineAsync(poA.PoLineId, unit: ZeroDecimalsUnit);
        await SetPoLineAsync(poB.PoLineId, unit: ZeroDecimalsUnit);

        var resp = await client.PostAsJsonAsync("/api/asns/from-schedule", FromSchedule(
            new[] { schedA, schedB },
            new Dictionary<Guid, decimal> { [schedA] = 2.5m, [schedB] = 3m }));   // only PO A's quantity is off
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        var errors = (await Read<AsnDetailDto>(resp)).Errors;
        errors.Should().Equal(new[] { ScaleMessage(poA, ZeroDecimalsUnit, 0) },
            because: "the offending line is named by its PO number and position; the valid PO's line is not reported");
        errors.Single().Should().NotContain(poB.PoNumber);
        (await AsnLineCountAsync(poA)).Should().Be(0);
        (await AsnLineCountAsync(poB)).Should().Be(0);

        // Fix the one quantity and the multi-PO ASN is created.
        var ok = await client.PostAsJsonAsync("/api/asns/from-schedule", FromSchedule(
            new[] { schedA, schedB },
            new Dictionary<Guid, decimal> { [schedA] = 2m, [schedB] = 3m }));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));
        (await Read<AsnDetailDto>(ok)).Data!.Lines.Select(l => l.ShippedQty).Should().BeEquivalentTo(new[] { 2m, 3m });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────
    private static CreateAsnFromScheduleRequest FromSchedule(IReadOnlyList<Guid> scheduleIds, IReadOnlyDictionary<Guid, decimal>? shipQty = null)
        => new(ScheduleIds: scheduleIds, ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2), ShipQtyByScheduleId: shipQty);

    /// <summary>
    /// A fresh tagged supplier + single-line PO, ACCEPTED through the real endpoint (so its Approved schedule exists), with
    /// the PO line re-pointed at <paramref name="unitCode"/> — created in the unit master with exactly
    /// <paramref name="decimals"/> places (idempotent) so the precision is what the test says it is.
    /// </summary>
    private async Task<(ProcureToPayFlow.Setup Po, HttpClient Client, Guid ScheduleId)> ScheduledPoWithUnitAsync(string unitCode, int decimals)
    {
        await UnitMasterDecimalsTests.EnsureUnitAsync(_fx, unitCode, decimals);
        var po = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var scheduleId = (await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, client, po.PoId))[po.PoLineId];
        await SetPoLineAsync(po.PoLineId, unit: unitCode);
        return (po, client, scheduleId);
    }

    /// <summary>Edits the test's own tagged PO line directly (unit and/or order quantity) — no other test reads it.</summary>
    private async Task SetPoLineAsync(Guid poLineId, string? unit = null, decimal? orderQty = null)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId);
        if (unit is not null) await line.ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderUnit, unit));
        if (orderQty is { } qty) await line.ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderQty, qty));
    }

    /// <summary>How many live ASN lines exist against this PO line (a rejected create must leave none).</summary>
    private async Task<int> AsnLineCountAsync(ProcureToPayFlow.Setup po)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AsnLines.IgnoreQueryFilters().CountAsync(l => l.PurchaseOrderLineId == po.PoLineId && !l.IsDeleted);
    }

    private static async Task AcceptAsync(HttpClient client, Guid poId)
    {
        var resp = await client.PostAsJsonAsync($"/api/purchase-orders/{poId}/accept",
            new MerinoOne.SupplierPortal.Contracts.PurchaseOrders.AcceptPoRequest());
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
    }

    private static CreateAsnRequest Req(Guid poId, List<CreateAsnLineRequest> lines) => new(
        PurchaseOrderId: poId,
        PurchaseOrderIds: null,
        ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
        TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
        DriverName: null, DriverPhone: null, Notes: null,
        Lines: lines);

    private async Task<(Guid Id, DateTime DeliveryDate)> ScheduleOfLineAsync(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sch = await db.DeliverySchedules.IgnoreQueryFilters()
            .SingleAsync(s => s.PurchaseOrderLineId == poLineId && !s.IsDeleted);
        return (sch.Id, sch.DeliveryDate);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
