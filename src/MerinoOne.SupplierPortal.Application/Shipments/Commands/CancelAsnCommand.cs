using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// Cancels an ASN that has not been posted yet (Draft / PendingApproval / Approved / Rejected -> Cancelled).
/// Terminal for supplier edits.
/// <para>2026-11-11 — a posted ASN (Submitted / InTransit) is refused (409, see <see cref="AsnLifecycle.Cancellable"/>):
/// it is already in LN and this command never notified LN, so cancelling it here only made the portal and the ERP
/// disagree. Balance is consumed only at Post (§10.4), so no cancellable ASN ever holds balance and no reversal is
/// needed — the Submitted-ASN reversal this handler used to run (UC-ASN-11) is gone with it.</para>
/// </summary>
public record CancelAsnCommand(Guid Id) : IRequest<Unit>;

public class CancelAsnCommandValidator : AbstractValidator<CancelAsnCommand>
{
    public CancelAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class CancelAsnCommandHandler : IRequestHandler<CancelAsnCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CancelAsnCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(CancelAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        // Only a not-yet-posted ASN may be cancelled; a posted (Submitted / InTransit) or terminal ASN is a 409.
        AsnLifecycle.AssertCanCancel(asn.AsnStatus);

        // §10.4 — balance is consumed only at Post, so a cancellable ASN never touched shippedQtyToDate:
        // a single SaveChanges flips the status, nothing to reverse.
        asn.AsnStatus = AsnStatus.Cancelled;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
