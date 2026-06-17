using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record UnmapSupplierCommand(Guid UserId, Guid SupplierId) : IRequest<Unit>;

public class UnmapSupplierCommandHandler : IRequestHandler<UnmapSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly SupplierMapService _maps;

    public UnmapSupplierCommandHandler(IAppDbContext db, SupplierMapService maps)
    {
        _db = db;
        _maps = maps;
    }

    public async Task<Unit> Handle(UnmapSupplierCommand request, CancellationToken ct)
    {
        var map = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == request.UserId && m.SupplierId == request.SupplierId, ct)
            ?? throw new NotFoundException("SupplierUserMap", $"{request.UserId}|{request.SupplierId}");

        // TSD §5.2: SupplierUserMap and SecRight are removed together, in one transaction (shared helper).
        await _maps.RemoveMapAsync(map, ct);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
