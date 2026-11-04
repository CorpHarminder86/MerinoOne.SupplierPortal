using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// R18 (2026-11-03) — the mechanics every command that ACTS on a negotiation round shares. A negotiation is a chain of rounds;
/// the OPEN round (Submitted) was authored by one party and is answered by the OTHER, who may approve/accept it (→ ERP),
/// reject/decline it (→ the PO reverts), or counter it (→ a new round). The buyer's and the supplier's commands differ only
/// in WHO may act and on WHICH author's round, so the load-and-guard, the two resolutions that end a negotiation and the
/// concurrency mapping live here once.
/// </summary>
internal static class PoNegotiationRounds
{
    /// <summary>The audit actor for the caller — the user code, or "system" when there is none.</summary>
    public static string ActorOf(ICurrentUser user) => string.IsNullOrEmpty(user.UserCode) ? "system" : user.UserCode;

    /// <summary>A proposal's free-text note as stored: trimmed, null when blank.</summary>
    public static string? NotesOf(string? notes) => string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

    /// <summary>The party that answers a round authored by <paramref name="party"/>.</summary>
    public static NegotiationParty Other(NegotiationParty party)
        => party == NegotiationParty.Supplier ? NegotiationParty.Buyer : NegotiationParty.Supplier;

    /// <summary>
    /// Loads round <paramref name="id"/> for the party that must ANSWER it and proves it is open: Submitted (else 409) and
    /// authored by <paramref name="author"/> (else 409) — a party never answers its own round, so the buyer cannot approve its
    /// own counter and the supplier cannot accept its own proposal. <paramref name="verb"/> ("approved", "declined", ...) words
    /// the 409. Read through the normal seccode filters, like every command here.
    /// </summary>
    public static async Task<PurchaseOrderNegotiation> LoadOpenAsync(
        IAppDbContext db, Guid id, NegotiationParty author, string verb, bool includeLines, CancellationToken ct)
    {
        IQueryable<PurchaseOrderNegotiation> rounds = db.PurchaseOrderNegotiations;
        if (includeLines) rounds = rounds.Include(n => n.Lines);

        var round = await rounds.FirstOrDefaultAsync(n => n.Id == id, ct)
                    ?? throw new NotFoundException("PurchaseOrderNegotiation", id);

        if (round.NegotiationStatus != PoNegotiationStatus.Submitted)
            throw new ConflictException($"Only a Submitted negotiation can be {verb} (current: {round.NegotiationStatus}).");

        if (round.ProposedBy != author)
            throw new ConflictException(
                $"Only a {author}-proposed negotiation round can be {verb} (this round was proposed by the {round.ProposedBy}).");

        return round;
    }

    public static async Task<PurchaseOrder> LoadPoAsync(IAppDbContext db, Guid purchaseOrderId, bool includeLines, CancellationToken ct)
    {
        IQueryable<PurchaseOrder> orders = db.PurchaseOrders;
        if (includeLines) orders = orders.Include(p => p.Lines);

        return await orders.FirstOrDefaultAsync(p => p.Id == purchaseOrderId, ct)
               ?? throw new NotFoundException("PurchaseOrder", purchaseOrderId);
    }

    /// <summary>
    /// R4 (2026-06-26) — D1: raising a proposal (a supplier's create or its counter) is gated on the supplier's AllowNegotiate
    /// toggle. A supplier configured no-negotiate cannot put a proposal forward (UC-PO-04). 409 (conflicting config), mirrors
    /// the AllowReject gate on /reject. Answering a round the buyer already proposed (accept / decline) is not gated.
    /// </summary>
    public static void EnsureSupplierMayNegotiate(SupplierEntity supplier)
    {
        if (!supplier.AllowNegotiate)
            throw new ConflictException("This supplier is not permitted to negotiate purchase orders.");
    }

    /// <summary>
    /// Permission-based supplier write-path authorization for the portal-originated negotiation aggregate: the
    /// <c>PurchaseOrder.Negotiate</c> self-service permission + a verified <c>SupplierUserMap</c> membership (see
    /// <see cref="SupplierWriteGuard"/>). Throws <see cref="ForbiddenException"/> (403).
    /// </summary>
    public static Task EnsureSupplierCanWriteAsync(SupplierWriteGuard guard, Guid supplierId, Guid seccodeId, CancellationToken ct)
        => guard.EnsureCanWriteAsync(supplierId, seccodeId, Perm.PurchaseOrderNegotiate, ct);

    /// <summary>Marks a round answered: its new <paramref name="status"/> plus who answered it and when.</summary>
    public static void Close(PurchaseOrderNegotiation round, PoNegotiationStatus status, string actor, DateTime now)
    {
        round.NegotiationStatus = status;
        round.ReviewedAt = now;
        round.ReviewedBy = actor;
        round.UpdatedBy = actor;
        round.UpdatedOn = now;
    }

    /// <summary>
    /// Approves the open round — the buyer approving a supplier round, or the supplier accepting a buyer round: round →
    /// <see cref="PoNegotiationStatus.Approved"/>, PO → <see cref="PoStatus.Approved"/>, and — in the SAME transaction (mirrors
    /// <c>AcceptPoCommand</c>) — an <c>OutboxMessage</c> (deterministic key) is enqueued. The post-commit dispatcher performs the
    /// ERP round-trip (it rebuilds the payload from the round, so payloadJson is null here) and writes the InforSyncLog. NO ERP
    /// HTTP call inside the unit of work, and — per the locked decision — the local <c>PurchaseOrderLine</c> rows are NOT
    /// mutated (ERP re-syncs the revised PO inbound). The accepted round alone carries the whole proposal (every round stores
    /// its deltas against the live PO line), so the ERP payload is the same whichever party closed the negotiation.
    /// </summary>
    public static async Task ApproveAsync(
        IAppDbContext db, IOutboxDispatcher outbox, PurchaseOrderNegotiation round, NegotiationParty approvedBy, string actor,
        CancellationToken ct)
    {
        var po = await LoadPoAsync(db, round.PurchaseOrderId, includeLines: false, ct);
        var now = DateTime.UtcNow;

        Close(round, PoNegotiationStatus.Approved, actor, now);

        po.PoStatus = PoStatus.Approved;   // ERP re-syncs the revised PO inbound; PO lines are NOT mutated here.
        po.UpdatedBy = actor;
        po.UpdatedOn = now;

        // Deterministic key — REUSED across retries so LN dedupes; tenant-qualified. The business key includes the
        // negotiation id (NOT just PoNumber) so a second negotiation on the same PO — the normal reject/cancel-then-
        // renegotiate path — gets its own outbox row instead of colliding with a prior round's key (which the
        // dispatcher's idempotency probe would silently no-op, dropping the ERP push + sync-log). entityId = the
        // negotiation id so the dispatcher rebuilds the payload from the negotiation + lines. The round is closed BEFORE the
        // enqueue: the LN eligibility gate reads its (tracked) status, which has to say Approved.
        var key = OutboxKey.For(
            OutboxEntity.PurchaseOrder, po.TenantId, $"{po.PoNumber}|{round.Id:N}", "negotiation-approve");
        await outbox.EnqueueAsync(
            OutboxTransactionType.PoNegotiationApprove, OutboxEntity.PurchaseOrder, round.Id, key, payloadJson: null, ct);

        // PO "History" tab: mark the approval (the per-line proposal rows were written on submit).
        var (outcome, detail) = approvedBy == NegotiationParty.Buyer
            ? ("approved", "Buyer approved — queued to ERP.")
            : ("accepted", "Supplier accepted — queued to ERP.");
        PoNegotiationHistory.RecordOutcome(db, po, round, outcome, detail, actor, now);

        await SaveAsync(db, ct);   // round + PO state + outbox row — one transaction; dispatch is post-commit.
    }

    /// <summary>
    /// Turns the open round down — the buyer rejecting a supplier round, or the supplier declining a buyer round: round →
    /// <see cref="PoNegotiationStatus.Rejected"/> (reason stored) and the PO reverts to its captured
    /// <c>PreviousPoStatus</c> (the supplier again sees Ack/Accept/Reject/Negotiate). Nothing is pushed to ERP.
    /// </summary>
    public static Task RejectAsync(
        IAppDbContext db, PurchaseOrderNegotiation round, NegotiationParty rejectedBy, string reason, string actor, CancellationToken ct)
    {
        round.RejectionReason = reason.Trim();
        return RevertAsync(
            db, round, PoNegotiationStatus.Rejected, rejectedBy == NegotiationParty.Buyer ? "rejected" : "declined",
            round.RejectionReason, actor, ct);
    }

    /// <summary>
    /// Ends the negotiation without an ERP push — rejected, declined or withdrawn: the round takes <paramref name="status"/> and the
    /// PO reverts to its captured <c>PreviousPoStatus</c> (avoid hardcoding Acknowledged). The PO "History" tab gets one
    /// <paramref name="outcome"/> row, carrying the <paramref name="detail"/> (the reason, which is captured nowhere else on the PO).
    /// </summary>
    public static async Task RevertAsync(
        IAppDbContext db, PurchaseOrderNegotiation round, PoNegotiationStatus status, string outcome, string? detail, string actor,
        CancellationToken ct)
    {
        var po = await LoadPoAsync(db, round.PurchaseOrderId, includeLines: false, ct);
        var now = DateTime.UtcNow;

        Close(round, status, actor, now);

        po.PoStatus = round.PreviousPoStatus;
        po.UpdatedBy = actor;
        po.UpdatedOn = now;

        PoNegotiationHistory.RecordOutcome(db, po, round, outcome, detail, actor, now);

        await SaveAsync(db, ct);
    }

    /// <summary>
    /// SaveChanges that turns the loser of a RowVersion race into a 409. A round is answered exactly once: two users answering it
    /// concurrently (two reviewers approving, a supplier declining while the buyer withdraws...) produce a
    /// <see cref="DbUpdateConcurrencyException"/> on the second one's save, whose answer — and, for an approval, whose outbox row —
    /// is rolled back in full; the first one's answer already stands.
    /// </summary>
    public static async Task SaveAsync(IAppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(
                "This negotiation round was answered concurrently by another user; no further action was taken.");
        }
    }

    /// <summary>The round as the API returns it after an action: header + its stored (delta) lines, the supplier's name resolved.</summary>
    public static async Task<PoNegotiationDto> ToDtoAsync(IAppDbContext db, PurchaseOrderNegotiation round, CancellationToken ct)
    {
        var supplierName = await db.Suppliers
            .Where(s => s.Id == round.SupplierId)
            .Select(s => s.LegalName)
            .FirstOrDefaultAsync(ct);
        return PoNegotiationMapper.ToDto(round, supplierName ?? string.Empty);
    }
}
