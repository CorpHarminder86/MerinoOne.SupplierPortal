using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record MapSupplierCommand(Guid UserId, MapSupplierRequest Body) : IRequest<Unit>;

public class MapSupplierCommandValidator : AbstractValidator<MapSupplierCommand>
{
    public MapSupplierCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Body.SupplierId).NotEmpty();
        RuleFor(x => x.Body.TenantEntityId).NotEmpty()
            .WithMessage("A company (TenantEntityId) is required — map the user under the supplier's company.");
    }
}

public class MapSupplierCommandHandler : IRequestHandler<MapSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public MapSupplierCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(MapSupplierCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        var supplier = await _db.Suppliers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == request.Body.SupplierId, ct)
            ?? throw new NotFoundException("Supplier", request.Body.SupplierId);

        // === Company scoping (TenantCompany §5) ============================================
        // The mapping is made under a specific company; the supplier MUST belong to that company.
        // This closes the "supplier spanning companies" hole — a supplier lives in exactly one company.
        var companyId = request.Body.TenantEntityId;

        // Company must exist; its tenant must equal the acting tenant. IgnoreQueryFilters + explicit
        // tenant restriction so a cross-tenant company can never be referenced.
        var actingTenant = _user.TenantId;
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => !e.IsDeleted && e.Id == companyId, ct)
            ?? throw new NotFoundException("Company", companyId);

        if (actingTenant.HasValue && company.TenantId != actingTenant.Value)
            throw new ConflictException("The selected company belongs to a different tenant.");

        if (supplier.TenantEntityId != companyId)
            throw new ConflictException(
                $"Supplier '{supplier.SupplierCode}' does not belong to the selected company. A supplier maps to exactly one company.");

        // Load any existing mapping INCLUDING soft-deleted. An active row is a true conflict.
        // A soft-deleted row means the admin previously removed the mapping — restore it + the
        // paired SecRight (one in, one out per TSD §5.2) instead of inserting a duplicate.
        var existingMap = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.SupplierId == supplier.Id, ct);

        if (existingMap is not null && !existingMap.IsDeleted)
            throw new ConflictException($"User '{user.UserCode}' is already mapped to supplier '{supplier.SupplierCode}'.");

        var seccode = await _db.Seccodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SupplierId == supplier.Id && s.SeccodeType == SeccodeType.G, ct)
            ?? throw new NotFoundException("Supplier Seccode (G)", supplier.Id);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        if (existingMap is not null)
        {
            // Restore the soft-deleted map + its paired SecRight (or create a fresh SecRight if
            // the original is gone — keeps the map row stable while still rebuilding access).
            var existingSecRight = await _db.SecRights.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == existingMap.SecRightId, ct);

            if (existingSecRight is not null)
            {
                existingSecRight.IsDeleted = false;
                existingSecRight.DeletedBy = null;
                existingSecRight.DeletedOn = null;
                existingSecRight.CanRead = true;
                existingSecRight.CanWrite = request.Body.CanWrite;
                existingSecRight.UpdatedBy = actor;
                existingSecRight.UpdatedOn = now;
            }
            else
            {
                var freshSecRight = new SecRight
                {
                    Id = Guid.NewGuid(),
                    SeccodeId = seccode.Id,
                    UserCode = user.UserCode,
                    CanRead = true,
                    CanWrite = request.Body.CanWrite,
                    CreatedBy = actor,
                    CreatedOn = now
                };
                _db.SecRights.Add(freshSecRight);
                existingMap.SecRightId = freshSecRight.Id;
            }

            existingMap.IsDeleted = false;
            existingMap.DeletedBy = null;
            existingMap.DeletedOn = null;
            existingMap.UpdatedBy = actor;
            existingMap.UpdatedOn = now;
        }
        else
        {
            var secRight = new SecRight
            {
                Id = Guid.NewGuid(),
                SeccodeId = seccode.Id,
                UserCode = user.UserCode,
                CanRead = true,
                CanWrite = request.Body.CanWrite,
                CreatedBy = actor,
                CreatedOn = now
            };
            _db.SecRights.Add(secRight);

            _db.SupplierUserMaps.Add(new SupplierUserMap
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                AppUserId = user.Id,
                SecRightId = secRight.Id,
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        // (c) Auto-grant company access if the user lacks it. A supplier user needs a UserCompanyMap for
        //     its supplier's company so the always-on company filter doesn't hide its own data. Restore a
        //     soft-deleted map rather than duplicating (UQ_UserCompanyMap_user_company).
        var existingCompanyMap = await _db.UserCompanyMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.TenantEntityId == companyId, ct);

        if (existingCompanyMap is null)
        {
            var hasAnyCompany = await _db.UserCompanyMaps.IgnoreQueryFilters()
                .AnyAsync(m => m.AppUserId == user.Id && !m.IsDeleted, ct);

            _db.UserCompanyMaps.Add(new UserCompanyMap
            {
                Id = Guid.NewGuid(),
                TenantId = company.TenantId,        // explicit — don't rely on the interceptor cross-tenant
                AppUserId = user.Id,
                TenantEntityId = companyId,
                IsDefault = !hasAnyCompany,         // first company becomes the default active company
                CreatedBy = actor,
                CreatedOn = now
            });
        }
        else if (existingCompanyMap.IsDeleted)
        {
            existingCompanyMap.IsDeleted = false;
            existingCompanyMap.DeletedBy = null;
            existingCompanyMap.DeletedOn = null;
            existingCompanyMap.TenantId = company.TenantId;
            existingCompanyMap.UpdatedBy = actor;
            existingCompanyMap.UpdatedOn = now;
        }

        // Single SaveChangesAsync — EF wraps in an implicit transaction.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
