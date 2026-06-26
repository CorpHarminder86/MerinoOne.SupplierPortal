using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §14 / UC-PO-04 (Phase 6): the 48h negotiation-SLA buyer nudge. Drives the
/// <see cref="SlaNudgeWorker"/>'s testable pass (<c>NudgeOnceAsync</c>) directly against REAL SQL (the hosted loop
/// is awkward to wait on deterministically — mirrors how <c>OutboxStaleDispatchedSweepTests</c> drives the outbox
/// reconcile sweep). Asserts the selection predicate's THREE branches in one pass:
/// <list type="number">
///   <item>an overdue Submitted negotiation with a resolvable buyer → SELECTED: one Pending email enqueued + NudgeSentAt stamped;</item>
///   <item>a negotiation already stamped (NudgeSentAt set) → DEDUPED: never re-nudged (no second email);</item>
///   <item>an overdue Submitted negotiation whose buyer can't be resolved (PO has no BuyerUserId) → STAMPED-AND-SKIPPED
///         (NudgeSentAt set so it isn't re-selected forever; NO email — the hot-loop guard).</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SlaNudgeWorkerTests
{
    private const string TemplateKey = "PoNegotiationNudge";

    private readonly IntegrationTestFixture _fx;
    public SlaNudgeWorkerTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task SlaNudge_selects_overdue_dedupes_stamped_and_stamps_unresolvable_buyer()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var slaWindow = TimeSpan.FromHours(48);
        var overdue = DateTime.UtcNow - slaWindow - TimeSpan.FromHours(1);   // > 48h old → past the SLA window.

        // The seeded Buyer A user has an active e-mail; use it as the resolvable buyer for branch 1.
        var buyerEmail = "sec-buyer-a@merino.local";

        Guid selectedNegId, dedupedNegId, unresolvableNegId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var supplier = await _fx.CreateSupplierAsync(tag,
                IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

            // Branch 1 — PO with a resolvable BuyerUserId; an overdue Submitted negotiation, never nudged.
            var poWithBuyer = await SeedPoAsync(db, supplier, $"PO-SLA-A-{tag}", SecurityTestHarness.BuyerUserId);
            selectedNegId = await SeedNegAsync(db, poWithBuyer, supplier, overdue, nudgeSentAt: null);

            // Branch 2 — overdue Submitted but ALREADY stamped (NudgeSentAt set) → must be skipped.
            var poDeduped = await SeedPoAsync(db, supplier, $"PO-SLA-B-{tag}", SecurityTestHarness.BuyerUserId);
            dedupedNegId = await SeedNegAsync(db, poDeduped, supplier, overdue, nudgeSentAt: overdue.AddHours(1));

            // Branch 3 — overdue Submitted, NO BuyerUserId → un-resolvable buyer; stamp-and-skip (no e-mail).
            var poNoBuyer = await SeedPoAsync(db, supplier, $"PO-SLA-C-{tag}", buyerUserId: null);
            unresolvableNegId = await SeedNegAsync(db, poNoBuyer, supplier, overdue, nudgeSentAt: null);

            await db.SaveChangesAsync();
        }

        // Drive one nudge pass against the real DB (own scope → own context, like the worker).
        int enqueued;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var renderer = scope.ServiceProvider.GetRequiredService<IEmailTemplateRenderer>();
            enqueued = await SlaNudgeWorker.NudgeOnceAsync(
                db, renderer, slaWindow, batchSize: 50, NullLogger.Instance, CancellationToken.None);
        }

        enqueued.Should().Be(1, because: "only the resolvable-buyer overdue negotiation enqueues a reminder this pass");

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Branch 1 — selected: NudgeSentAt stamped + EXACTLY ONE Pending nudge e-mail to the buyer.
            var selected = await db.PurchaseOrderNegotiations.IgnoreQueryFilters().FirstAsync(n => n.Id == selectedNegId);
            selected.NudgeSentAt.Should().NotBeNull(because: "the selected negotiation is stamped after enqueue");
            var mailCount = await db.EmailOutbox.IgnoreQueryFilters()
                .CountAsync(m => m.ToEmail == buyerEmail && m.TemplateKey == TemplateKey
                                 && m.Subject.Contains(selected.PoNumber));
            mailCount.Should().Be(1, because: "exactly one reminder e-mail is enqueued for the buyer (UC-PO-04)");

            // Branch 2 — deduped: the stamp is UNCHANGED (still the seeded value) → never re-nudged.
            var deduped = await db.PurchaseOrderNegotiations.IgnoreQueryFilters().FirstAsync(n => n.Id == dedupedNegId);
            deduped.NudgeSentAt.Should().NotBeNull();
            deduped.NudgeSentAt!.Value.Should().BeCloseTo(overdue.AddHours(1), TimeSpan.FromSeconds(2),
                because: "an already-nudged negotiation is not re-stamped/re-sent (dedupe)");

            // Branch 3 — un-resolvable buyer: STAMPED (so it won't re-select forever) but NO e-mail.
            var unresolvable = await db.PurchaseOrderNegotiations.IgnoreQueryFilters().FirstAsync(n => n.Id == unresolvableNegId);
            unresolvable.NudgeSentAt.Should().NotBeNull(because: "the un-resolvable-buyer negotiation is stamped to avoid a hot-loop");
            var noMail = await db.EmailOutbox.IgnoreQueryFilters()
                .AnyAsync(m => m.TemplateKey == TemplateKey && m.Subject.Contains(unresolvable.PoNumber));
            noMail.Should().BeFalse(because: "no reminder e-mail is enqueued when the buyer can't be resolved");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private static async Task<PurchaseOrder> SeedPoAsync(
        AppDbContext db, SecurityTestHarness.SeededSupplier supplier, string poNumber, Guid? buyerUserId)
    {
        var now = DateTime.UtcNow;
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(), PoNumber = poNumber, SupplierId = supplier.SupplierId, PoType = PoType.Material,
            PoDate = now.Date, PoStatus = PoStatus.Negotiation, BuyerUserId = buyerUserId,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        };
        db.PurchaseOrders.Add(po);
        return po;
    }

    private static async Task<Guid> SeedNegAsync(
        AppDbContext db, PurchaseOrder po, SecurityTestHarness.SeededSupplier supplier,
        DateTime submittedAt, DateTime? nudgeSentAt)
    {
        var id = Guid.NewGuid();
        db.PurchaseOrderNegotiations.Add(new PurchaseOrderNegotiation
        {
            Id = id, PurchaseOrderId = po.Id, PoNumber = po.PoNumber, SupplierId = supplier.SupplierId,
            NegotiationStatus = PoNegotiationStatus.Submitted, PreviousPoStatus = PoStatus.Released,
            SubmittedAt = submittedAt, NudgeSentAt = nudgeSentAt,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = submittedAt,
        });
        await Task.CompletedTask;
        return id;
    }
}
