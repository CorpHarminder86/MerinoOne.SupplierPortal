using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// R18 (2026-11-03) — the supplier accepts the buyer's counter-offer: the open round, which the BUYER proposed,
/// goes <see cref="PoNegotiationStatus.Submitted"/> → <see cref="PoNegotiationStatus.Approved"/>, the PO goes to
/// <see cref="PoStatus.Approved"/> and the ERP push is enqueued — exactly what the buyer's approval of a supplier round does
/// (<see cref="PoNegotiationRounds.ApproveAsync"/> is shared, so the outbox key and payload are identical). Accepting a round
/// the supplier proposed itself, or one that is no longer open, is a 409; a concurrent answer to the same round is a 409 too.
///
/// <para><b>Supplier write path:</b> authorised through <see cref="SupplierWriteGuard"/> exactly as the create is (the
/// <c>PurchaseOrder.Negotiate</c> permission + a verified <c>SupplierUserMap</c> membership). The supplier's AllowNegotiate toggle
/// does not gate this — accepting the buyer's terms raises no counter-proposal.</para>
/// </summary>
public record AcceptPoNegotiationCommand(Guid Id) : IRequest<PoNegotiationDto>;

public class AcceptPoNegotiationCommandHandler : IRequestHandler<AcceptPoNegotiationCommand, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IOutboxDispatcher _outbox;
    private readonly SupplierWriteGuard _guard;

    public AcceptPoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user, IOutboxDispatcher outbox, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _outbox = outbox; _guard = guard;
    }

    public async Task<PoNegotiationDto> Handle(AcceptPoNegotiationCommand request, CancellationToken ct)
    {
        var round = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, NegotiationParty.Buyer, "accepted", includeLines: true, ct);

        await PoNegotiationRounds.EnsureSupplierCanWriteAsync(_guard, round.SupplierId, round.SeccodeId, ct);

        await PoNegotiationRounds.ApproveAsync(
            _db, _outbox, round, NegotiationParty.Supplier, PoNegotiationRounds.ActorOf(_user), ct);
        return await PoNegotiationRounds.ToDtoAsync(_db, round, ct);
    }
}
