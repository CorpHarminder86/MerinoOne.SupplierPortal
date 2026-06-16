using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Platform.Commands;

/// <summary>
/// Create a physical company (TenantEntity / Infor LN logistic company) under a tenant. Shared by the
/// Platform onboarding flow (explicit TenantId, cross-tenant) and the Tenant-Admin Companies flow
/// (TenantId is the acting tenant). The TenantId is stamped EXPLICITLY here rather than relying on the
/// ScopeStampInterceptor, because the Platform path operates cross-tenant.
/// </summary>
public record CreateTenantEntityCommand(Guid TenantId, string Code, string Name) : IRequest<Guid>;

public class CreateTenantEntityCommandValidator : AbstractValidator<CreateTenantEntityCommand>
{
    public CreateTenantEntityCommandValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public class CreateTenantEntityCommandHandler : IRequestHandler<CreateTenantEntityCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateTenantEntityCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Guid> Handle(CreateTenantEntityCommand request, CancellationToken ct)
    {
        var code = request.Code.Trim();
        var name = request.Name.Trim();

        // IgnoreQueryFilters: the Platform path bypasses the tenant filter anyway; for the Tenant-Admin
        // path this still works because we restrict by TenantId explicitly. Re-apply !IsDeleted.
        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => !t.IsDeleted && t.Id == request.TenantId, ct)
            ?? throw new NotFoundException("Tenant", request.TenantId);

        // UQ_TenantEntity_tenant_code — code unique within the tenant.
        if (await _db.TenantEntities.IgnoreQueryFilters()
                .AnyAsync(e => !e.IsDeleted && e.TenantId == tenant.Id && e.Code == code, ct))
            throw new ConflictException($"Company code '{code}' already exists in this tenant.");

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "platform" : _user.UserCode;
        var id = Guid.NewGuid();

        _db.TenantEntities.Add(new TenantEntity
        {
            Id = id,
            TenantId = tenant.Id, // explicit cross-tenant stamp — do NOT rely on the interceptor
            Code = code,
            Name = name,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        await _db.SaveChangesAsync(ct);
        return id;
    }
}
