using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record AssignRoleCommand(Guid UserId, AssignRoleRequest Body) : IRequest<Unit>;

public class AssignRoleCommandHandler : IRequestHandler<AssignRoleCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public AssignRoleCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(AssignRoleCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        var role = await _db.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == request.Body.RoleId, ct)
            ?? throw new NotFoundException("Role", request.Body.RoleId);

        // Load existing link INCLUDING soft-deleted. If active → idempotent. If soft-deleted →
        // restore by clearing the delete-block (resurrects the original Seq + audit chain).
        var existing = await _db.UserRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(ur => ur.AppUserId == user.Id && ur.RoleId == role.Id, ct);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        if (existing is not null)
        {
            if (!existing.IsDeleted) return Unit.Value; // already active — idempotent
            existing.IsDeleted = false;
            existing.DeletedBy = null;
            existing.DeletedOn = null;
            existing.UpdatedBy = actor;
            existing.UpdatedOn = now;
        }
        else
        {
            _db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                AppUserId = user.Id,
                RoleId = role.Id,
                CreatedOn = now,
                CreatedBy = actor
            });
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
