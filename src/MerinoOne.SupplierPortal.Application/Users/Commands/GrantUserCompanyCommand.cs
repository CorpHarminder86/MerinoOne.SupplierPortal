using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

/// <summary>
/// Enhancement Round 2 / Feature A — direct, full-access company grant. Grants a user FULL access to a
/// company: every supplier in it (incl. future ones), seccode-bypassed but scoped to that company only via
/// <c>UserCompanyMap.AllSuppliers = true</c>. Distinct from <see cref="MapSupplierCommand"/>, which only
/// grants seccode-scoped, supplier-derived access.
///
/// Create-or-restore semantics on the (user, company) <see cref="UserCompanyMap"/>:
///   • active row exists       → upgrade <c>AllSuppliers</c> to true (a supplier-derived map becomes full).
///   • soft-deleted row exists → restore + <c>AllSuppliers = true</c>.
///   • no row                  → add a new full-access map; first company becomes the default.
/// </summary>
public record GrantUserCompanyCommand(Guid UserId, Guid TenantEntityId) : IRequest<Unit>;

public class GrantUserCompanyCommandValidator : AbstractValidator<GrantUserCompanyCommand>
{
    public GrantUserCompanyCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.TenantEntityId).NotEmpty();
    }
}

public class GrantUserCompanyCommandHandler : IRequestHandler<GrantUserCompanyCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GrantUserCompanyCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(GrantUserCompanyCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        // Company must exist; its tenant must equal the acting tenant. IgnoreQueryFilters + explicit
        // !IsDeleted so a cross-tenant company can never be referenced. Mirrors MapSupplierCommand.
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => !e.IsDeleted && e.Id == request.TenantEntityId, ct)
            ?? throw new NotFoundException("Company", request.TenantEntityId);

        var actingTenant = _user.TenantId;
        if (actingTenant.HasValue && company.TenantId != actingTenant.Value)
            throw new ConflictException("The selected company belongs to a different tenant.");

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;

        var existing = await _db.UserCompanyMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.TenantEntityId == request.TenantEntityId, ct);

        if (existing is not null && !existing.IsDeleted)
        {
            // Upgrade a supplier-derived map to a full-access grant (idempotent when already true).
            if (!existing.AllSuppliers)
            {
                existing.AllSuppliers = true;
                existing.UpdatedBy = actor;
                existing.UpdatedOn = now;
            }
        }
        else if (existing is not null)
        {
            // Restore the soft-deleted map as a full-access grant.
            existing.IsDeleted = false;
            existing.DeletedBy = null;
            existing.DeletedOn = null;
            existing.AllSuppliers = true;
            existing.TenantId = company.TenantId;
            existing.UpdatedBy = actor;
            existing.UpdatedOn = now;
        }
        else
        {
            var hasAnyCompany = await _db.UserCompanyMaps.IgnoreQueryFilters()
                .AnyAsync(m => m.AppUserId == user.Id && !m.IsDeleted, ct);

            _db.UserCompanyMaps.Add(new UserCompanyMap
            {
                Id = Guid.NewGuid(),
                TenantId = company.TenantId,
                AppUserId = user.Id,
                TenantEntityId = request.TenantEntityId,
                AllSuppliers = true,
                IsDefault = !hasAnyCompany,     // first company becomes the default active company
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
