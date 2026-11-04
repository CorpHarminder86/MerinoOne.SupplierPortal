using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// Supplier withdraws an in-flight negotiation: <see cref="PoNegotiationStatus.Submitted"/> →
/// <see cref="PoNegotiationStatus.Cancelled"/> and the PO reverts to its captured
/// <c>PreviousPoStatus</c> (so the supplier sees Ack/Accept/Reject/Negotiate again). Nothing is pushed to ERP.
///
/// <para><b>Rounds (R18):</b> the supplier withdraws a round it proposed ITSELF; a round the buyer proposed is answered with
/// accept / decline / counter instead, so cancelling it is a 409.</para>
/// </summary>
public record CancelPoNegotiationCommand(Guid Id) : IRequest<Unit>;

public class CancelPoNegotiationCommandHandler : IRequestHandler<CancelPoNegotiationCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CancelPoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(CancelPoNegotiationCommand request, CancellationToken ct)
    {
        var round = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, NegotiationParty.Supplier, "cancelled", includeLines: false, ct);

        // PO "History" tab: mark the withdrawal.
        await PoNegotiationRounds.RevertAsync(
            _db, round, PoNegotiationStatus.Cancelled, "cancelled", "Withdrawn by supplier.",
            PoNegotiationRounds.ActorOf(_user), ct);
        return Unit.Value;
    }
}
