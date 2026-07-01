using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Roles.Common;

/// <summary>
/// Single source of truth for turning permission CODES into role↔permission rows. Shared by
/// CreateRole and AssignPermissions so the resolve/validate and materialise rules live once.
/// </summary>
public sealed class RolePermissionWriter
{
    private readonly IAppDbContext _db;
    public RolePermissionWriter(IAppDbContext db) => _db = db;

    /// <summary>
    /// Resolve permission codes to their ids, throwing a ValidationException listing any unknown code.
    /// Server-side filtered query (uses the UQ_Permission_code index; no full-table load) and excludes
    /// soft-deleted permissions so a retired code can never be assigned.
    /// </summary>
    public async Task<List<Guid>> ResolveAsync(IEnumerable<string>? codes, CancellationToken ct)
    {
        var requested = (codes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (requested.Count == 0) return new List<Guid>();

        var rows = await _db.Permissions
            .Where(p => !p.IsDeleted && requested.Contains(p.Code))
            .Select(p => new { p.Id, p.Code })
            .ToListAsync(ct);

        var missing = requested.Except(rows.Select(r => r.Code)).ToArray();
        if (missing.Length > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["permissionCodes"] = new[] { $"Unknown permissions: {string.Join(", ", missing)}." }
            });
        }

        return rows.Select(r => r.Id).ToList();
    }

    /// <summary>
    /// Apply the desired permission-id set to a role as a DELTA: resurrect soft-deleted rows that are
    /// wanted again, soft-delete rows no longer wanted, insert only the genuinely new ones. This bounds
    /// the write to the actual change, preserves audit history on unchanged grants, and never collides
    /// with the (now filtered) unique index by re-inserting a still-present (RoleId, PermissionId) pair.
    /// The caller owns the surrounding SaveChanges.
    /// </summary>
    public async Task ApplyAsync(Guid roleId, IReadOnlyCollection<Guid> permissionIds, string actor, DateTime now, CancellationToken ct)
    {
        var want = permissionIds.ToHashSet();

        var existing = await _db.RolePermissions.IgnoreQueryFilters()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        foreach (var rp in existing)
        {
            var wanted = want.Contains(rp.PermissionId);
            if (wanted && rp.IsDeleted)
            {
                // Resurrect a previously-removed grant instead of inserting a colliding new row.
                rp.IsDeleted = false;
                rp.DeletedBy = null;
                rp.DeletedOn = null;
                rp.UpdatedBy = actor;
                rp.UpdatedOn = now;
            }
            else if (!wanted && !rp.IsDeleted)
            {
                // Soft-delete (interceptor turns Remove into isDeleted=1) the grants no longer wanted.
                _db.RolePermissions.Remove(rp);
            }
        }

        var have = existing.Select(e => e.PermissionId).ToHashSet();
        foreach (var pid in want.Where(p => !have.Contains(p)))
        {
            _db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = roleId,
                PermissionId = pid,
                CreatedBy = actor,
                CreatedOn = now
            });
        }
    }
}
