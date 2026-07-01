using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;

namespace MerinoOne.SupplierPortal.Application.Permissions.Commands;

/// <summary>
/// Register a new GLOBAL permission code in the catalog. Gated by Role.Write (the role/permission
/// management right). Permissions are global primitives with a globally-unique Code; there is no
/// per-tenant custom permission. A created code enforces nothing until a matching
/// [Authorize(Policy = ...)] gate references it in code.
/// </summary>
public record CreatePermissionCommand(CreatePermissionRequest Body) : IRequest<string>;

public class CreatePermissionCommandValidator : AbstractValidator<CreatePermissionCommand>
{
    // Dotted PascalCase, e.g. "Report.Export" or "Integration.Inbound.Foo".
    private static readonly Regex CodePattern =
        new(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$", RegexOptions.Compiled);

    public CreatePermissionCommandValidator()
    {
        RuleFor(x => x.Body.Code)
            .NotEmpty().WithMessage("Permission code is required.")
            .MaximumLength(100)
            .Must(c => c is not null && CodePattern.IsMatch(c))
            .WithMessage("Code must be dotted PascalCase, e.g. 'Module.Action'.");
        RuleFor(x => x.Body.Name)
            .NotEmpty().WithMessage("Permission name is required.")
            .MaximumLength(150);
        RuleFor(x => x.Body.Module).MaximumLength(50);
        RuleFor(x => x.Body.Description).MaximumLength(400);
    }
}

public class CreatePermissionCommandHandler : IRequestHandler<CreatePermissionCommand, string>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CreatePermissionCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<string> Handle(CreatePermissionCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();

        // UQ_Permission_code is global + unfiltered, so a soft-deleted row with the same code would also
        // collide — check across all rows (IgnoreQueryFilters).
        var exists = await _db.Permissions.IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct);
        if (exists) throw new ConflictException($"Permission '{code}' already exists.");

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        _db.Permissions.Add(new Permission
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = request.Body.Name.Trim(),
            Module = string.IsNullOrWhiteSpace(request.Body.Module) ? null : request.Body.Module.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Body.Description) ? null : request.Body.Description.Trim(),
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return code;
    }
}
