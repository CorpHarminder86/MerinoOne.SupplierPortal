using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Common.Security;

/// <summary>
/// Enforces <c>SecRight.canWrite</c> on a supplier-owned aggregate (the supplier's type-G seccode) BEFORE any
/// mutation of a portal-originated child (bank detail, license). Resolution order (R4 risk R7, decided option b):
/// <list type="number">
///   <item>Privileged internal users (Admin/Manager) — always allowed (they see/write all rows).</item>
///   <item>A user holding a <c>SecRight.CanWrite=true</c> on the supplier's G-seccode — the standard RLS write grant.</item>
///   <item>A supplier user with the <c>Supplier.ChangeRequest</c> permission AND a verified <c>SupplierUserMap</c>
///         membership — authorizes the write of THESE portal-originated aggregates without toggling the row-level
///         <c>canWrite</c> grant (suppliers are ERP-read-only on their master, so canWrite stays false).</item>
/// </list>
/// Throws <see cref="ForbiddenException"/> (mapped to HTTP 403 by GlobalExceptionHandler) otherwise.
/// </summary>
public sealed class SupplierWriteGuard
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public SupplierWriteGuard(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task EnsureCanWriteAsync(Guid supplierId, Guid supplierSeccodeId, CancellationToken ct)
    {
        // 1. Privileged internal users write anything.
        if (_user.IsAdmin || _user.IsManager) return;

        var userCode = _user.UserCode;

        // 2. Standard RLS write grant: a SecRight with CanWrite=true on the supplier's G-seccode.
        var hasWriteGrant = await _db.SecRights
            .IgnoreQueryFilters()
            .AnyAsync(r => r.SeccodeId == supplierSeccodeId
                           && r.UserCode == userCode
                           && r.CanWrite
                           && !r.IsDeleted, ct);
        if (hasWriteGrant) return;

        // 3. Supplier self-service: Supplier.ChangeRequest permission + a verified SupplierUserMap membership.
        if (_user.HasPermission("Supplier.ChangeRequest"))
        {
            var isMappedMember = await (
                from m in _db.SupplierUserMaps.IgnoreQueryFilters().Where(m => m.SupplierId == supplierId && !m.IsDeleted)
                join u in _db.AppUsers.IgnoreQueryFilters().Where(u => u.UserCode == userCode && !u.IsDeleted)
                    on m.AppUserId equals u.Id
                select m.Id).AnyAsync(ct);
            if (isMappedMember) return;
        }

        throw new ForbiddenException("You do not have write access to this supplier's records.");
    }
}
