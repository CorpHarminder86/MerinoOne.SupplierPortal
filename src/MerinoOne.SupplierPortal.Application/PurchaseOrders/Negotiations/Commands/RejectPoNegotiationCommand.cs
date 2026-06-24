using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// Buyer rejects a submitted negotiation: <see cref="PoNegotiationStatus.Submitted"/> →
/// <see cref="PoNegotiationStatus.Rejected"/> (reason stored) and the PO reverts to its captured
/// <c>PreviousPoStatus</c> (the supplier again sees Ack/Accept/Reject/Negotiate). Nothing is pushed to ERP.
/// Reason is required (≤1000).
/// </summary>
public record RejectPoNegotiationCommand(Guid Id, RejectPoNegotiationRequest Body) : IRequest<Unit>;

public class RejectPoNegotiationCommandValidator : AbstractValidator<RejectPoNegotiationCommand>
{
    public RejectPoNegotiationCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000)
            .WithMessage("A rejection reason is required.");
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
        var negotiation = await _db.PurchaseOrderNegotiations.FirstOrDefaultAsync(n => n.Id == request.Id, ct)
                          ?? throw new NotFoundException("PurchaseOrderNegotiation", request.Id);

        if (negotiation.NegotiationStatus != PoNegotiationStatus.Submitted)
            throw new ConflictException(
                $"Only a Submitted negotiation can be rejected (current: {negotiation.NegotiationStatus}).");

        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == negotiation.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", negotiation.PurchaseOrderId);

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

        negotiation.NegotiationStatus = PoNegotiationStatus.Rejected;
        negotiation.RejectionReason = request.Body.Reason.Trim();
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
