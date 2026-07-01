using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Roles.Common;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Roles.Commands;

public record AssignPermissionsCommand(Guid RoleId, AssignPermissionsRequest Body) : IRequest<Unit>;

public class AssignPermissionsCommandValidator : AbstractValidator<AssignPermissionsCommand>
{
    public AssignPermissionsCommandValidator()
    {
        RuleFor(x => x.Body.PermissionCodes).NotNull();
    }
}

public class AssignPermissionsCommandHandler : IRequestHandler<AssignPermissionsCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly RolePermissionWriter _writer;
    private readonly IEffectivePermissionService _perms;

    public AssignPermissionsCommandHandler(
        IAppDbContext db, ICurrentUser user, RolePermissionWriter writer, IEffectivePermissionService perms)
    {
        _db = db;
        _user = user;
        _writer = writer;
        _perms = perms;
    }

    public async Task<Unit> Handle(AssignPermissionsCommand request, CancellationToken ct)
    {
        var role = await _db.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == request.RoleId, ct)
            ?? throw new NotFoundException("Role", request.RoleId);

        var permIds = await _writer.ResolveAsync(request.Body.PermissionCodes, ct);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        // Delta apply (resurrect / soft-delete / add) — no full-set churn, no unique-index collision.
        await _writer.ApplyAsync(role.Id, permIds, actor, now, ct);

        role.UpdatedOn = now;
        role.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);

        // Live RBAC (no relogin): evict every holder's cached permission set so the change applies on
        // their next request.
        var affected = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.RoleId == role.Id && !ur.IsDeleted)
            .Select(ur => ur.AppUserId)
            .ToListAsync(ct);
        await _perms.InvalidateAsync(affected, ct);

        return Unit.Value;
    }
}
