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

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────
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
