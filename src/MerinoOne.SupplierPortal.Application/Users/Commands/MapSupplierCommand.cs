using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record MapSupplierCommand(Guid UserId, MapSupplierRequest Body) : IRequest<Unit>;

public class MapSupplierCommandHandler : IRequestHandler<MapSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public MapSupplierCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(MapSupplierCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        var supplier = await _db.Suppliers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == request.Body.SupplierId, ct)
            ?? throw new NotFoundException("Supplier", request.Body.SupplierId);

        // Existing mapping is a conflict (TSD §5.2: removed together, in one transaction — likewise added together).
        var alreadyMapped = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .AnyAsync(m => m.AppUserId == user.Id && m.SupplierId == supplier.Id, ct);
        if (alreadyMapped)
            throw new ConflictException($"User '{user.UserCode}' is already mapped to supplier '{supplier.SupplierCode}'.");

        var seccode = await _db.Seccodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SupplierId == supplier.Id && s.SeccodeType == SeccodeType.G, ct)
            ?? throw new NotFoundException("Supplier Seccode (G)", supplier.Id);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        var secRight = new SecRight
        {
            Id = Guid.NewGuid(),
            SeccodeId = seccode.Id,
            UserCode = user.UserCode,
            CanRead = true,
            CanWrite = request.Body.CanWrite,
            CreatedBy = actor,
            CreatedOn = now
        };
        _db.SecRights.Add(secRight);

        _db.SupplierUserMaps.Add(new SupplierUserMap
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            AppUserId = user.Id,
            SecRightId = secRight.Id,
            CreatedBy = actor,
            CreatedOn = now
        });

        // Single SaveChangesAsync — EF wraps in an implicit transaction.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
