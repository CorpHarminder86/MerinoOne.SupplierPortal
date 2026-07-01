using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Roles.Common;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Roles.Commands;

public record CreateRoleCommand(CreateRoleRequest Body) : IRequest<Guid>;

public class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(x => x.Body.Name)
            .NotEmpty().WithMessage("Role name is required.")
            .MaximumLength(50);
        RuleFor(x => x.Body.PermissionCodes).NotNull();
    }
}

public class CreateRoleCommandHandler : IRequestHandler<CreateRoleCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly RolePermissionWriter _writer;

    public CreateRoleCommandHandler(IAppDbContext db, ICurrentUser user, RolePermissionWriter writer)
    {
        _db = db;
        _user = user;
        _writer = writer;
    }

    public async Task<Guid> Handle(CreateRoleCommand request, CancellationToken ct)
    {
        var body = request.Body;

        // Roles are per-tenant. Require a tenant context and stamp it explicitly (rather than relying on
        // the interceptor) so the invariant is visible + testable and a system/platform principal cannot
        // silently create a NULL-tenant, unfiltered role.
        var tenantId = _user.TenantId
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["tenant"] = new[] { "A role must be created within a tenant context." }
            });

        // Scope the uniqueness check to the current tenant to match UQ_Role_tenant_name (TenantId, Name)
        // WHERE isDeleted = 0 — a global check wrongly blocked another tenant from reusing a role name and
        // tripped on soft-deleted rows.
        var clash = await _db.Roles.IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.Name == body.Name && !r.IsDeleted, ct);
        if (clash) throw new ConflictException($"Role '{body.Name}' already exists.");

        var permIds = await _writer.ResolveAsync(body.PermissionCodes, ct);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;
        var roleId = Guid.NewGuid();

        _db.Roles.Add(new Role
        {
            Id = roleId,
            TenantId = tenantId,
            Name = body.Name,
            CreatedBy = actor,
            CreatedOn = now
        });

        await _writer.ApplyAsync(roleId, permIds, actor, now, ct);

        await _db.SaveChangesAsync(ct);
        return roleId;
    }
}
