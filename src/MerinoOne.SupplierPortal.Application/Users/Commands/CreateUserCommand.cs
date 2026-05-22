using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record CreateUserCommand(CreateUserRequest Body) : IRequest<Guid>;

public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Body.UserCode)
            .NotEmpty().WithMessage("UserCode is required.")
            .Length(3, 50).WithMessage("UserCode must be 3-50 chars.")
            .Matches("^[a-zA-Z0-9_]+$").WithMessage("UserCode must be alphanumeric or underscore.");
        RuleFor(x => x.Body.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Body.Password).NotEmpty().MinimumLength(8).WithMessage("Password must be at least 8 chars.");
        RuleFor(x => x.Body.Roles).NotEmpty().WithMessage("At least one role is required.");
    }
}

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPasswordHasher _hasher;

    public CreateUserCommandHandler(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
    }

    public async Task<Guid> Handle(CreateUserCommand request, CancellationToken ct)
    {
        var body = request.Body;

        if (await _db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.UserCode == body.UserCode, ct))
            throw new ConflictException($"UserCode '{body.UserCode}' is already in use.");
        if (await _db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.Email == body.Email, ct))
            throw new ConflictException($"Email '{body.Email}' is already in use.");

        // Accept role identifiers as either Name or Guid string.
        var requested = body.Roles.Distinct().ToArray();
        var roleRows = await _db.Roles.IgnoreQueryFilters()
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);
        var matched = roleRows
            .Where(r => requested.Contains(r.Name) || requested.Contains(r.Id.ToString()))
            .ToList();
        var missing = requested
            .Where(req => !matched.Any(m => m.Name == req || m.Id.ToString() == req))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["roles"] = new[] { $"Unknown roles: {string.Join(", ", missing)}." }
            });
        }

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var userId = Guid.NewGuid();

        var user = new AppUser
        {
            Id = userId,
            UserCode = body.UserCode,
            FullName = body.FullName,
            Email = body.Email,
            PasswordHash = _hasher.Hash(body.Password),
            IsInternal = body.IsInternal,
            IsMfaEnabled = body.IsMfaEnabled,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        };
        _db.AppUsers.Add(user);

        foreach (var role in matched)
        {
            _db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                RoleId = role.Id,
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        // Mirror UserSeeder: default U-seccode + self SecRight.
        var seccodeId = Guid.NewGuid();
        _db.Seccodes.Add(new Seccode
        {
            Id = seccodeId,
            SeccodeType = SeccodeType.U,
            Name = body.UserCode + " default",
            AppUserId = userId,
            CreatedBy = actor,
            CreatedOn = now
        });
        _db.SecRights.Add(new SecRight
        {
            Id = Guid.NewGuid(),
            SeccodeId = seccodeId,
            UserCode = body.UserCode,
            CanRead = true,
            CanWrite = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        await _db.SaveChangesAsync(ct);
        return userId;
    }
}
