using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record RemoveRoleCommand(Guid UserId, Guid RoleId) : IRequest<Unit>;

public class RemoveRoleCommandHandler : IRequestHandler<RemoveRoleCommand, Unit>
{
    private readonly IAppDbContext _db;
    public RemoveRoleCommandHandler(IAppDbContext db) => _db = db;

    public async Task<Unit> Handle(RemoveRoleCommand request, CancellationToken ct)
    {
        var link = await _db.UserRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(ur => ur.AppUserId == request.UserId && ur.RoleId == request.RoleId, ct)
            ?? throw new NotFoundException("UserRole", $"{request.UserId}|{request.RoleId}");

        _db.UserRoles.Remove(link);
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
