using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

// R17 (2026-10-26) — Carrier master admin deactivate (soft: IsActive=false; ASN snapshots keep their carrierCode).
public record DeactivateCarrierCommand(Guid Id) : IRequest<Unit>;

public class DeactivateCarrierCommandHandler : IRequestHandler<DeactivateCarrierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeactivateCarrierCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(DeactivateCarrierCommand request, CancellationToken ct)
    {
        var c = await _db.Carriers.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Carrier", request.Id);

        c.IsActive = false;
        c.UpdatedBy = _user.UserCode;
        c.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
