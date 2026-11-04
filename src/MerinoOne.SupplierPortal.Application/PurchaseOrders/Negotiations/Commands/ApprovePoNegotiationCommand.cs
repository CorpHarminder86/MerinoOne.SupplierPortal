using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;

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
/// approving the same negotiation concurrently produce a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// on the loser's SaveChanges — mapped to a 409 (the winner already approved + enqueued).</para>
///
/// <para><b>Rounds (R18):</b> the buyer answers a round the SUPPLIER proposed (a round it proposed itself is answered by the
/// supplier — <see cref="AcceptPoNegotiationCommand"/>); anything else is a 409. The approval itself is the one the supplier's
/// accept shares: <see cref="PoNegotiationRounds.ApproveAsync"/>.</para>
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
        var round = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, NegotiationParty.Supplier, "approved", includeLines: false, ct);

        await PoNegotiationRounds.ApproveAsync(
            _db, _outbox, round, NegotiationParty.Buyer, PoNegotiationRounds.ActorOf(_user), ct);
        return Unit.Value;
    }
}
