using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// Buyer rejects a submitted negotiation: <see cref="PoNegotiationStatus.Submitted"/> →
/// <see cref="PoNegotiationStatus.Rejected"/> (reason stored) and the PO reverts to its captured
/// <c>PreviousPoStatus</c> (the supplier again sees Ack/Accept/Reject/Negotiate). Nothing is pushed to ERP.
/// Reason is required (≤1000).
///
/// <para><b>Rounds (R18):</b> the buyer rejects a round the SUPPLIER proposed (the supplier declines a buyer's counter —
/// <see cref="DeclinePoNegotiationCommand"/>); anything else is a 409. The rejection itself is the one the decline shares:
/// <see cref="PoNegotiationRounds.RejectAsync"/>.</para>
/// </summary>
public record RejectPoNegotiationCommand(Guid Id, RejectPoNegotiationRequest Body) : IRequest<Unit>;

public class RejectPoNegotiationCommandValidator : AbstractValidator<RejectPoNegotiationCommand>
{
    public RejectPoNegotiationCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).RejectionReason();
    }
}

public class RejectPoNegotiationCommandHandler : IRequestHandler<RejectPoNegotiationCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RejectPoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(RejectPoNegotiationCommand request, CancellationToken ct)
    {
        var round = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, NegotiationParty.Supplier, "rejected", includeLines: false, ct);

        await PoNegotiationRounds.RejectAsync(
            _db, round, NegotiationParty.Buyer, request.Body.Reason, PoNegotiationRounds.ActorOf(_user), ct);
        return Unit.Value;
    }
}
