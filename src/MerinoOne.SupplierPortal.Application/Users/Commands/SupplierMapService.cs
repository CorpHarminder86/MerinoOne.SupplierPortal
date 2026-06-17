using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

/// <summary>
/// Shared user↔supplier mapping primitives, factored out of <see cref="MapSupplierCommand"/> and
/// <see cref="UnmapSupplierCommand"/> so the bulk <see cref="SetCompanySupplierMapsCommand"/> reuses the
/// exact same create-or-restore / remove-both / auto-company-map logic with NO behaviour change.
///
/// None of these methods call <c>SaveChanges</c> — the caller owns the unit of work / transaction so a
/// batch commits atomically. All reads use <c>IgnoreQueryFilters</c> (admin/cross-cutting) and re-apply
/// the explicit soft-delete discipline where needed.
/// </summary>
public class SupplierMapService
{
    private readonly IAppDbContext _db;

    public SupplierMapService(IAppDbContext db) => _db = db;

    /// <summary>
    /// Create-or-restore the user↔supplier <see cref="SupplierUserMap"/> + its paired <see cref="SecRight"/>
    /// (one in, one out, TSD §5.2). Identical to MapSupplierCommand's create/restore branch:
    ///   • soft-deleted map → restore it + restore/recreate the SecRight (CanRead=true, CanWrite=canWrite).
    ///   • no map           → add a fresh SecRight + SupplierUserMap.
    /// The caller must have already validated the supplier belongs to the company and is not already
    /// actively mapped. Throws NotFound if the supplier's group (type-G) Seccode is missing.
    /// </summary>
    public async Task CreateOrRestoreMapAsync(
        AppUser user, SupplierEntity supplier, bool canWrite, string actor, DateTime now, CancellationToken ct)
    {
        var seccode = await _db.Seccodes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SupplierId == supplier.Id && s.SeccodeType == SeccodeType.G, ct)
            ?? throw new NotFoundException("Supplier Seccode (G)", supplier.Id);

        var existingMap = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.SupplierId == supplier.Id, ct);

        if (existingMap is not null)
        {
            // Restore the soft-deleted map + its paired SecRight (or create a fresh SecRight if gone).
            var existingSecRight = await _db.SecRights.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == existingMap.SecRightId, ct);

            if (existingSecRight is not null)
            {
                existingSecRight.IsDeleted = false;
                existingSecRight.DeletedBy = null;
                existingSecRight.DeletedOn = null;
                existingSecRight.CanRead = true;
                existingSecRight.CanWrite = canWrite;
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
                    CanWrite = canWrite,
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
                CanWrite = canWrite,
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
    }

    /// <summary>
    /// Hard-delete the user↔supplier map + its paired SecRight together (TSD §5.2), exactly as
    /// <see cref="UnmapSupplierCommand"/>. The map MUST be supplied by the caller (already loaded).
    /// </summary>
    public async Task RemoveMapAsync(SupplierUserMap map, CancellationToken ct)
    {
        var secRight = await _db.SecRights.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == map.SecRightId, ct);

        _db.SupplierUserMaps.Remove(map);
        if (secRight != null)
            _db.SecRights.Remove(secRight);
    }

    /// <summary>
    /// Auto-grant company access if the user lacks it, mirroring MapSupplierCommand's (c) path. A
    /// supplier-derived map is created with <c>AllSuppliers = false</c>; a soft-deleted map is restored
    /// WITHOUT changing an existing AllSuppliers value (never downgrade a full-access grant true→false).
    /// </summary>
    public async Task EnsureCompanyMapAsync(
        AppUser user, TenantEntity company, string actor, DateTime now, CancellationToken ct)
    {
        var existingCompanyMap = await _db.UserCompanyMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.TenantEntityId == company.Id, ct);

        if (existingCompanyMap is null)
        {
            var hasAnyCompany = await _db.UserCompanyMaps.IgnoreQueryFilters()
                .AnyAsync(m => m.AppUserId == user.Id && !m.IsDeleted, ct);

            _db.UserCompanyMaps.Add(new UserCompanyMap
            {
                Id = Guid.NewGuid(),
                TenantId = company.TenantId,        // explicit — don't rely on the interceptor cross-tenant
                AppUserId = user.Id,
                TenantEntityId = company.Id,
                AllSuppliers = false,               // supplier-derived access
                IsDefault = !hasAnyCompany,         // first company becomes the default active company
                CreatedBy = actor,
                CreatedOn = now
            });
        }
        else if (existingCompanyMap.IsDeleted)
        {
            // Restore — DO NOT touch AllSuppliers (never downgrade a prior full-access grant).
            existingCompanyMap.IsDeleted = false;
            existingCompanyMap.DeletedBy = null;
            existingCompanyMap.DeletedOn = null;
            existingCompanyMap.TenantId = company.TenantId;
            existingCompanyMap.UpdatedBy = actor;
            existingCompanyMap.UpdatedOn = now;
        }
    }
}
