using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3. Cancels an ASN (Draft or Submitted -> Cancelled). Terminal for supplier edits
/// (Q2: no un-submit; Cancel is the only exit once Submitted). A Cancelled ASN's auto-created draft invoice is
/// left as-is (it is portal-internal and never auto-posted; admin handles it separately).
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

        if (asn.AsnStatus is not (AsnStatus.Draft or AsnStatus.Submitted))
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; only Draft or Submitted ASNs can be cancelled.");

        asn.AsnStatus = AsnStatus.Cancelled;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
