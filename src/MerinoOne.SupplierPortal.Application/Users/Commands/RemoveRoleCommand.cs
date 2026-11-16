using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record RemoveRoleCommand(Guid UserId, Guid RoleId) : IRequest<Unit>;

public class RemoveRoleCommandHandler : IRequestHandler<RemoveRoleCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IEffectivePermissionService _perms;
    private readonly ICurrentUser _user;

    public RemoveRoleCommandHandler(IAppDbContext db, IEffectivePermissionService perms, ICurrentUser user)
    {
        _db = db;
        _perms = perms;
        _user = user;
    }

    public async Task<Unit> Handle(RemoveRoleCommand request, CancellationToken ct)
    {
        // Tenant rule (IgnoreQueryFilters below drops it): the target user must belong to the caller's tenant.
        if (!await _db.AppUsers.IgnoreQueryFilters().InCallerTenant(_user).AnyAsync(u => u.Id == request.UserId, ct))
            throw new NotFoundException("User", request.UserId);

        var link = await _db.UserRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(ur => ur.AppUserId == request.UserId && ur.RoleId == request.RoleId, ct)
            ?? throw new NotFoundException("UserRole", $"{request.UserId}|{request.RoleId}");

        _db.UserRoles.Remove(link);
        await _db.SaveChangesAsync(ct);

        // Live RBAC (no relogin): revoking a role removes its permissions on the user's NEXT request.
        await _perms.InvalidateAsync(new[] { request.UserId }, ct);
        return Unit.Value;
    }
}
