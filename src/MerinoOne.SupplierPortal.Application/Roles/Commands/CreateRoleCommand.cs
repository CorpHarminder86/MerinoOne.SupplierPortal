using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
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

    public CreateRoleCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Guid> Handle(CreateRoleCommand request, CancellationToken ct)
    {
        var body = request.Body;
        if (await _db.Roles.IgnoreQueryFilters().AnyAsync(r => r.Name == body.Name, ct))
            throw new ConflictException($"Role '{body.Name}' already exists.");

        var requested = (body.PermissionCodes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);

        var allPerms = await _db.Permissions.IgnoreQueryFilters()
            .Select(p => new { p.Id, p.Code })
            .ToListAsync(ct);
        var permRows = allPerms.Where(p => requested.Contains(p.Code)).ToList();
        var missing = requested.Except(permRows.Select(p => p.Code)).ToArray();
        if (missing.Length > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["permissionCodes"] = new[] { $"Unknown permissions: {string.Join(", ", missing)}." }
            });
        }

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;
        var roleId = Guid.NewGuid();

        _db.Roles.Add(new Role
        {
            Id = roleId,
            Name = body.Name,
            CreatedBy = actor,
            CreatedOn = now
        });

        foreach (var p in permRows)
        {
            _db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = roleId,
                PermissionId = p.Id,
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        await _db.SaveChangesAsync(ct);
        return roleId;
    }
}
