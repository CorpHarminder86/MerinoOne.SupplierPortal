using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// Buyer approves a submitted negotiation: <see cref="PoNegotiationStatus.Submitted"/> →
/// <see cref="PoNegotiationStatus.Approved"/>, PO → <see cref="PoStatus.Approved"/>, and — in the SAME
/// transaction (mirrors <c>AcceptPoCommand</c>) — an <c>OutboxMessage</c> (deterministic key) is enqueued. The
/// post-commit dispatcher performs the ERP round-trip (it rebuilds the payload, so payloadJson is null here) and
/// writes the InforSyncLog. NO ERP HTTP call inside the unit of work, and — per the locked decision — the local
/// <c>PurchaseOrderLine</c> rows are NOT mutated (ERP re-syncs the revised PO inbound).
///
/// <para><b>Optimistic concurrency:</b> <c>PurchaseOrderNegotiation</c> carries a <c>RowVersion</c>. Two buyers
/// approving the same negotiation concurrently produce a <see cref="DbUpdateConcurrencyException"/> on the loser's
/// SaveChanges — mapped to a 409 (the winner already approved + enqueued).</para>
/// </summary>
public record ApprovePoNegotiationCommand(Guid Id) : IRequest<Unit>;

public class ApprovePoNegotiationCommandHandler : IRequestHandler<ApprovePoNegotiationCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IOutboxDispatcher _outbox;

    public ApprovePoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user, IOutboxDispatcher outbox)
    {
        _db = db; _user = user; _outbox = outbox;
    }

    public async Task<Unit> Handle(ApprovePoNegotiationCommand request, CancellationToken ct)
    {
        var negotiation = await _db.PurchaseOrderNegotiations.FirstOrDefaultAsync(n => n.Id == request.Id, ct)
                          ?? throw new NotFoundException("PurchaseOrderNegotiation", request.Id);

        if (negotiation.NegotiationStatus != PoNegotiationStatus.Submitted)
            throw new ConflictException(
                $"Only a Submitted negotiation can be approved (current: {negotiation.NegotiationStatus}).");

        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == negotiation.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", negotiation.PurchaseOrderId);

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

        negotiation.NegotiationStatus = PoNegotiationStatus.Approved;
        negotiation.ReviewedAt = now;
        negotiation.ReviewedBy = actor;
        negotiation.UpdatedBy = actor;
        negotiation.UpdatedOn = now;

        po.PoStatus = PoStatus.Approved;   // ERP re-syncs the revised PO inbound; PO lines are NOT mutated here.
        po.UpdatedBy = actor;
        po.UpdatedOn = now;

        // Deterministic key — REUSED across retries so LN dedupes; tenant-qualified. The business key includes the
        // negotiation id (NOT just PoNumber) so a second negotiation on the same PO — the normal reject/cancel-then-
        // renegotiate path — gets its own outbox row instead of colliding with a prior round's key (which the
        // dispatcher's idempotency probe would silently no-op, dropping the ERP push + sync-log). entityId = the
        // negotiation id so the dispatcher rebuilds the payload from the negotiation + lines.
        var key = OutboxKey.For(
            OutboxEntity.PurchaseOrder, po.TenantId, $"{po.PoNumber}|{negotiation.Id:N}", "negotiation-approve");
        await _outbox.EnqueueAsync(
            OutboxTransactionType.PoNegotiationApprove, OutboxEntity.PurchaseOrder, negotiation.Id, key, payloadJson: null, ct);

        // PO "History" tab: mark the approval (the per-line proposal rows were written on submit).
        PoNegotiationHistory.RecordOutcome(_db, po, "approved", "Buyer approved — queued to ERP.", actor, now);

        try
        {
            await _db.SaveChangesAsync(ct);   // negotiation + PO state + outbox row — one transaction; dispatch is post-commit.
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent approve already won the RowVersion race (approved + enqueued). Skip — re-enqueuing would
            // be a no-op anyway (the deterministic key is unique), but the state is already correct.
            throw new ConflictException(
                "This negotiation was approved concurrently by another reviewer; no further action was taken.");
        }

        return Unit.Value;
    }
}
