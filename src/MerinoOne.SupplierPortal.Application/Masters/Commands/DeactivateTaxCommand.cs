using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

/// <summary>Marks a Tax master row inactive (preserved on historical PO/invoice lines), mirroring DeliveryTerm.</summary>
public record DeactivateTaxCommand(Guid Id) : IRequest<Unit>;

public class DeactivateTaxCommandHandler : IRequestHandler<DeactivateTaxCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeactivateTaxCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(DeactivateTaxCommand request, CancellationToken ct)
    {
        var t = await _db.Taxes.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Tax", request.Id);

        t.IsActive = false;
        t.UpdatedBy = _user.UserCode;
        t.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
