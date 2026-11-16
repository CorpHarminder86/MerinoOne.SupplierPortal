using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// 2026-11-16 — LINE REMOVAL on an inbound PO re-push, through the REAL host (POST /api/integration/inbound/purchase-orders).
/// A stored line whose positionNo is absent from a NON-empty push was removed in LN: while the PO is Draft / Released /
/// Negotiation it is soft-deleted (with its delivery schedule and any open-negotiation line); once the PO is past supplier
/// acceptance the line is KEPT and the row carries a warning; a removable line with shipped / received / invoiced history
/// fails the row; an empty lines array never deletes.
///
/// <para>Every PO carries a per-test tag so it never collides with the persistent test DB's other rows.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoLineRemovalInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly int[] FivePositions = { 10, 20, 30, 40, 50 };
    private static readonly int[] FourPositions = { 10, 20, 30, 40 };

    private readonly IntegrationTestFixture _fx;
    public PoLineRemovalInboundTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (a) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Released_po_absent_line_is_soft_deleted_and_row_is_kept()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("A");

        var rows = await PushAsync(Body(tag, supplier, poNumber, FourPositions));
        var row = rows.Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            var lines = await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == po.Id).ToListAsync();
            lines.Should().HaveCount(5, because: "removal is a soft delete — the row is kept");
            var removed = lines.Single(l => l.PositionNo == 50);
            removed.IsDeleted.Should().BeTrue();
            // The audit interceptor re-stamps UpdatedBy with the calling API-key principal on save, so only assert it was stamped.
            removed.UpdatedBy.Should().NotBeNullOrEmpty();
            lines.Where(l => !l.IsDeleted).Select(l => l.PositionNo).Should().BeEquivalentTo(FourPositions);
        }

        (await LiveLineCountAsync(poNumber)).Should().Be(4);
    }

    /// <summary>
    /// The supplier's PO detail read (GET /api/purchase-orders/{id}) must not surface a removed (soft-deleted) line.
    /// </summary>
    [SkippableFact]
    public async Task Released_po_removed_line_is_not_returned_by_detail_api()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("A2");

        var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        var poId = await PoIdAsync(poNumber);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await client.GetAsync($"/api/purchase-orders/{poId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await Read<PurchaseOrderDetailDto>(resp)).Data!;
        detail.Lines.Select(l => l.PositionNo).Should().BeEquivalentTo(FourPositions,
            because: "a soft-deleted line must not be returned by the PO detail read");
    }

    // ── (b) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Accepted_po_keeps_absent_line_with_warning_and_stays_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("B");

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            po.PoStatus = PoStatus.Accepted;
            po.AcceptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Pushed twice (distinct Idempotency-Key each time) — the absent line must survive both, and the absence alone is not material.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
            row.Outcome.Should().Be(RowOutcome.Updated, because: $"attempt {attempt}: {row.Error}");
            row.Error.Should().Contain("PO is Accepted: line(s) 50 absent from the payload were kept", because: $"attempt {attempt}");
            row.Error.Should().Contain("kept");

            using var s = _fx.Factory.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            po.PoStatus.Should().Be(PoStatus.Accepted, because: "an absent line is not a material change");
            var lines = await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == po.Id && !l.IsDeleted).ToListAsync();
            lines.Select(l => l.PositionNo).Should().BeEquivalentTo(FivePositions, because: $"attempt {attempt}: line 50 is kept past supplier acceptance");
        }
    }

    // ── (c) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Removed_line_delivery_schedule_is_soft_deleted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("C");

        Guid scheduleId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            var line50 = await db.PurchaseOrderLines.IgnoreQueryFilters().SingleAsync(l => l.PurchaseOrderId == po.Id && l.PositionNo == 50);
            var sched = await db.DeliverySchedules.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.PurchaseOrderLineId == line50.Id && !d.IsDeleted);
            if (sched is null)
            {
                sched = new DeliverySchedule
                {
                    Id = Guid.NewGuid(), PurchaseOrderId = po.Id, PurchaseOrderLineId = line50.Id,
                    WarehouseAddressId = IntegrationTestFixture.ShipToAddressId,
                    ScheduledQty = line50.OrderQty, DeliveryDate = DateTime.UtcNow.Date.AddDays(7),
                    Status = DeliveryScheduleStatus.Approved,
                    SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
                };
                db.DeliverySchedules.Add(sched);
                await db.SaveChangesAsync();
            }
            scheduleId = sched.Id;
        }

        var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        using var s2 = _fx.Factory.Services.CreateScope();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db2.DeliverySchedules.IgnoreQueryFilters().SingleAsync(d => d.Id == scheduleId)).IsDeleted
            .Should().BeTrue(because: "the schedule of a removed line is removed with it");
    }

    // ── (d) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Removable_line_with_shipped_history_fails_the_row_and_leaves_po_untouched()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("D");

        int versionBefore;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            var line50 = await db.PurchaseOrderLines.IgnoreQueryFilters().SingleAsync(l => l.PurchaseOrderId == po.Id && l.PositionNo == 50);
            line50.ShippedQtyToDate = 3m;
            await db.SaveChangesAsync();
            versionBefore = po.Version;
        }

        var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Failed);
        row.Error.Should().StartWith("Cannot remove PO line(s) 50");

        using var s2 = _fx.Factory.Services.CreateScope();
        var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        var po2 = await db2.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        po2.Version.Should().Be(versionBefore, because: "a failed row leaves the PO untouched");
        var lines = await db2.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == po2.Id && !l.IsDeleted).ToListAsync();
        lines.Select(l => l.PositionNo).Should().BeEquivalentTo(FivePositions);
    }

    // ── (e) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Open_round_is_pruned_of_removed_line_but_stays_submitted_while_other_lines_remain()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("E");
        var roundId = await SeedRoundAsync(poNumber, supplier, positions: new[] { 40, 50 });

        var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var round = await db.PurchaseOrderNegotiations.IgnoreQueryFilters().Include(n => n.Lines).SingleAsync(n => n.Id == roundId);
        round.NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted, because: "line 40 is still being negotiated");
        round.Lines.Single(l => l.PositionNo == 50).IsDeleted.Should().BeTrue();
        round.Lines.Single(l => l.PositionNo == 40).IsDeleted.Should().BeFalse();
    }

    // ── (f) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Round_left_with_no_lines_is_cancelled_and_po_reverts_from_negotiation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("F");
        var roundId = await SeedRoundAsync(poNumber, supplier, positions: new[] { 50 });

        var row = (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var round = await db.PurchaseOrderNegotiations.IgnoreQueryFilters().Include(n => n.Lines).SingleAsync(n => n.Id == roundId);
        round.NegotiationStatus.Should().Be(PoNegotiationStatus.Cancelled);
        round.Lines.Should().OnlyContain(l => l.IsDeleted);
        var po = await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        po.PoStatus.Should().NotBe(PoStatus.Negotiation);
        po.PoStatus.Should().Be(PoStatus.Released, because: "the round's PreviousPoStatus is the revert target");
    }

    // ── (g) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Empty_lines_array_never_deletes()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("G");

        var body = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date, Array.Empty<PoLineRecord>(),
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var client = _fx.CreateInboundClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var resp = await client.PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        // Either the validator rejects it (400) or the handler accepts a header-only push — in both cases nothing is deleted.
        ((int)resp.StatusCode).Should().BeOneOf(200, 400);
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var r = (await Read<UpsertResultDto>(resp)).Data!;
            r.Rows.Single().Outcome.Should().NotBe(RowOutcome.Failed, because: r.Rows.Single().Error);
        }

        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var poId = (await db.PurchaseOrders.IgnoreQueryFilters().SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId)).Id;
        var lines = await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == poId && !l.IsDeleted).ToListAsync();
        lines.Select(l => l.PositionNo).Should().BeEquivalentTo(FivePositions, because: "an empty lines array must never wipe the PO");
    }

    // ── (h) ───────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Line_removed_then_re_pushed_is_recreated_as_a_new_row()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (tag, supplier, poNumber) = await SeedReleasedPoAsync("H");

        (await PushAsync(Body(tag, supplier, poNumber, FourPositions))).Single().Outcome.Should().Be(RowOutcome.Updated);
        var deletedId = await LineIdAsync(poNumber, 50, deleted: true);

        var row = (await PushAsync(Body(tag, supplier, poNumber, FivePositions))).Single();
        row.Outcome.Should().Be(RowOutcome.Updated, because: row.Error);

        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var poId = await PoIdAsync(poNumber);
        var at50 = await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == poId && l.PositionNo == 50).ToListAsync();
        at50.Should().HaveCount(2, because: "the soft-deleted row is kept and a fresh live row is inserted");
        var live = at50.Single(l => !l.IsDeleted);
        live.Id.Should().NotBe(deletedId);
        at50.Single(l => l.IsDeleted).Id.Should().Be(deletedId);
    }

    // ═════════════════════════════════ helpers ═════════════════════════════════

    private async Task<(string Tag, SecurityTestHarness.SeededSupplier Supplier, string PoNumber)> SeedReleasedPoAsync(string label)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poNumber = $"PO-LREM-{label}-{tag}";
        var rows = await PushAsync(Body(tag, supplier, poNumber, FivePositions));
        rows.Single().Outcome.Should().Be(RowOutcome.Inserted, because: rows.Single().Error);
        return (tag, supplier, poNumber);
    }

    private static PushPurchaseOrdersRequest Body(string tag, SecurityTestHarness.SeededSupplier supplier, string poNumber, int[] positions)
        => new(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                positions.Select(p => new PoLineRecord(PositionNo: p, SequenceNo: 1, ItemCode: $"ITM-{tag}-{p}",
                    OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 10,
                    Warehouse: IntegrationTestFixture.WarehouseCode)).ToArray(),
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });

    // A fresh Idempotency-Key per push so a re-push with an identical body is processed, not deduped.
    private async Task<IReadOnlyList<RowResult>> PushAsync(PushPurchaseOrdersRequest body)
    {
        var client = _fx.CreateInboundClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var resp = await client.PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var r = await Read<UpsertResultDto>(resp);
        r.Data!.Rows.Should().HaveCount(1);
        return r.Data!.Rows;
    }

    private async Task<Guid> PoIdAsync(string poNumber)
    {
        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PurchaseOrders.IgnoreQueryFilters()
            .SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId)).Id;
    }

    private async Task<int> LiveLineCountAsync(string poNumber)
    {
        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters()
            .CountAsync(l => l.PurchaseOrder!.PoNumber == poNumber && l.PurchaseOrder.TenantId == IntegrationTestFixture.TenantId && !l.IsDeleted);
    }

    private async Task<Guid> LineIdAsync(string poNumber, int positionNo, bool deleted)
    {
        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.PurchaseOrderLines.IgnoreQueryFilters()
            .SingleAsync(l => l.PurchaseOrder!.PoNumber == poNumber && l.PositionNo == positionNo && l.IsDeleted == deleted)).Id;
    }

    /// <summary>Flips the PO to Negotiation and opens a Submitted round (PreviousPoStatus = Released) proposing changes to <paramref name="positions"/>.</summary>
    private async Task<Guid> SeedRoundAsync(string poNumber, SecurityTestHarness.SeededSupplier supplier, int[] positions)
    {
        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .SingleAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        po.PoStatus = PoStatus.Negotiation;

        var now = DateTime.UtcNow;
        var round = new PurchaseOrderNegotiation
        {
            Id = Guid.NewGuid(), PurchaseOrderId = po.Id, PoNumber = po.PoNumber, SupplierId = supplier.SupplierId,
            NegotiationStatus = PoNegotiationStatus.Submitted, RoundNo = 1, ProposedBy = NegotiationParty.Supplier,
            PreviousPoStatus = PoStatus.Released, SubmittedAt = now,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        };
        foreach (var pos in positions)
        {
            var line = po.Lines.Single(l => l.PositionNo == pos && !l.IsDeleted);
            round.Lines.Add(new PurchaseOrderNegotiationLine
            {
                Id = Guid.NewGuid(), PurchaseOrderNegotiationId = round.Id, PurchaseOrderLineId = line.Id,
                PositionNo = line.PositionNo, SequenceNo = line.SequenceNo, ItemCode = line.ItemCode,
                OriginalQty = line.OrderQty, NegotiatedQty = line.OrderQty - 1,
                OriginalPrice = line.PriceUnit, NegotiatedPrice = line.PriceUnit,
                CreatedBy = "seed", CreatedOn = now,
            });
        }
        db.PurchaseOrderNegotiations.Add(round);
        await db.SaveChangesAsync();
        return round.Id;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
