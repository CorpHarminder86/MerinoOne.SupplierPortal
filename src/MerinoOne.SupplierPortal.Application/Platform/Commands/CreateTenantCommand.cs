using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Platform;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;

namespace MerinoOne.SupplierPortal.Application.Platform.Commands;

/// <summary>
/// Platform-Admin onboarding: create a new tenant. Runs cross-tenant (the caller is a Platform Admin
/// who bypasses the tenant filter), and the new <see cref="Tenant"/> carries no TenantId of its own —
/// a Tenant is the scope root, not a scoped row.
/// </summary>
public record CreateTenantCommand(CreateTenantRequest Body) : IRequest<Guid>;

public class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(200);
    }
}

public class CreateTenantCommandHandler : IRequestHandler<CreateTenantCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateTenantCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Guid> Handle(CreateTenantCommand request, CancellationToken ct)
    {
        var name = request.Body.Name.Trim();

        // Tenant names are globally unique. IgnoreQueryFilters: a Platform Admin already bypasses the
        // tenant filter, but Tenant has no soft-delete-relevant scope; re-apply !IsDeleted for safety.
        if (await _db.Tenants.IgnoreQueryFilters().AnyAsync(t => !t.IsDeleted && t.Name == name, ct))
            throw new ConflictException($"A tenant named '{name}' already exists.");

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "platform" : _user.UserCode;
        var id = Guid.NewGuid();

        _db.Tenants.Add(new Tenant
        {
            Id = id,
            Name = name,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        await _db.SaveChangesAsync(ct);
        return id;
    }
}
