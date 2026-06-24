using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// Supplier withdraws an in-flight negotiation: <see cref="PoNegotiationStatus.Submitted"/> →
/// <see cref="PoNegotiationStatus.Cancelled"/> and the PO reverts to its captured
/// <c>PreviousPoStatus</c> (so the supplier sees Ack/Accept/Reject/Negotiate again). Nothing is pushed to ERP.
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
        var negotiation = await _db.PurchaseOrderNegotiations.FirstOrDefaultAsync(n => n.Id == request.Id, ct)
                          ?? throw new NotFoundException("PurchaseOrderNegotiation", request.Id);

        if (negotiation.NegotiationStatus != PoNegotiationStatus.Submitted)
            throw new ConflictException(
                $"Only a Submitted negotiation can be cancelled (current: {negotiation.NegotiationStatus}).");

        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == negotiation.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", negotiation.PurchaseOrderId);

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

        negotiation.NegotiationStatus = PoNegotiationStatus.Cancelled;
        negotiation.ReviewedAt = now;
        negotiation.ReviewedBy = actor;
        negotiation.UpdatedBy = actor;
        negotiation.UpdatedOn = now;

        po.PoStatus = negotiation.PreviousPoStatus;   // revert (avoid hardcoding Acknowledged).
        po.UpdatedBy = actor;
        po.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
