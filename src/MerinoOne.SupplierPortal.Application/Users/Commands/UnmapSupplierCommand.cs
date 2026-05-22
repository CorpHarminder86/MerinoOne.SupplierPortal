using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record UnmapSupplierCommand(Guid UserId, Guid SupplierId) : IRequest<Unit>;

public class UnmapSupplierCommandHandler : IRequestHandler<UnmapSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    public UnmapSupplierCommandHandler(IAppDbContext db) => _db = db;

    public async Task<Unit> Handle(UnmapSupplierCommand request, CancellationToken ct)
    {
        var map = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == request.UserId && m.SupplierId == request.SupplierId, ct)
            ?? throw new NotFoundException("SupplierUserMap", $"{request.UserId}|{request.SupplierId}");

        // TSD §5.2: SupplierUserMap and SecRight are removed together, in one transaction.
        var secRight = await _db.SecRights.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == map.SecRightId, ct);

        _db.SupplierUserMaps.Remove(map);
        if (secRight != null)
            _db.SecRights.Remove(secRight);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
