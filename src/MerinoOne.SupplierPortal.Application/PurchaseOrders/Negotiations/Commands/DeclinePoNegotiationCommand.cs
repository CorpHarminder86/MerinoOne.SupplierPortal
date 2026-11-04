using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// R18 (2026-11-03) — the supplier declines the buyer's counter-offer: the open round, which the BUYER proposed,
/// goes <see cref="PoNegotiationStatus.Submitted"/> → <see cref="PoNegotiationStatus.Rejected"/> (reason stored) and the PO
/// reverts to its captured <c>PreviousPoStatus</c>, exactly as the buyer's rejection of a supplier round does
/// (<see cref="PoNegotiationRounds.RejectAsync"/> is shared). Nothing is pushed to ERP. Reason is required (≤1000), like a
/// reject's. Declining a round the supplier proposed itself, or one that is no longer open, is a 409.
///
/// <para><b>Supplier write path:</b> authorised through <see cref="SupplierWriteGuard"/> exactly as the create is.</para>
/// </summary>
public record DeclinePoNegotiationCommand(Guid Id, DeclinePoNegotiationRequest Body) : IRequest<PoNegotiationDto>;

public class DeclinePoNegotiationCommandValidator : AbstractValidator<DeclinePoNegotiationCommand>
{
    public DeclinePoNegotiationCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).RejectionReason();
    }
}

public class DeclinePoNegotiationCommandHandler : IRequestHandler<DeclinePoNegotiationCommand, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public DeclinePoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<PoNegotiationDto> Handle(DeclinePoNegotiationCommand request, CancellationToken ct)
    {
        var round = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, NegotiationParty.Buyer, "declined", includeLines: true, ct);

        await PoNegotiationRounds.EnsureSupplierCanWriteAsync(_guard, round.SupplierId, round.SeccodeId, ct);

        await PoNegotiationRounds.RejectAsync(
            _db, round, NegotiationParty.Supplier, request.Body.Reason, PoNegotiationRounds.ActorOf(_user), ct);
        return await PoNegotiationRounds.ToDtoAsync(_db, round, ct);
    }
}
