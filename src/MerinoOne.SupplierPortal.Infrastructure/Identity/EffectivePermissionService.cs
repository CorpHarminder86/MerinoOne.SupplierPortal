using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// Cache-backed resolver for a user's effective permission codes. The JWT no longer carries
/// permission claims; they are resolved here per request (one cache hit) and enriched onto the
/// principal, so an admin's grant/revoke applies on the user's next request — no relogin.
/// The cache is a short-TTL backstop; the authoritative freshness comes from explicit invalidation
/// on every AssignPermissions / AssignRole / RemoveRole / deactivate.
/// </summary>
public sealed class EffectivePermissionService : IEffectivePermissionService
{
    private readonly IAppDbContext _db;
    private readonly IDistributedCache _cache;

    private static readonly DistributedCacheEntryOptions Ttl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    public EffectivePermissionService(IAppDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string Key(Guid userId) => $"perms:{userId:N}";

    public async Task<IReadOnlySet<string>> GetAsync(Guid userId, CancellationToken ct)
    {
        var cached = await _cache.GetStringAsync(Key(userId), ct);
        if (cached is not null)
            return cached.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : cached.Split('\n').ToHashSet(StringComparer.Ordinal);

        var perms = await ResolveFromDbAsync(userId, ct);
        await _cache.SetStringAsync(Key(userId), string.Join('\n', perms), Ttl, ct);
        return perms;
    }

    public Task InvalidateAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        => Task.WhenAll(userIds.Distinct().Select(id => _cache.RemoveAsync(Key(id), ct)));

    // Soft-delete-aware; IgnoreQueryFilters bypasses BOTH seccode AND soft-delete, so re-apply !IsDeleted.
    private async Task<HashSet<string>> ResolveFromDbAsync(Guid userId, CancellationToken ct) =>
        (await (from ur in _db.UserRoles.IgnoreQueryFilters()
                join rp in _db.RolePermissions.IgnoreQueryFilters() on ur.RoleId equals rp.RoleId
                join p in _db.Permissions.IgnoreQueryFilters() on rp.PermissionId equals p.Id
                where ur.AppUserId == userId && !ur.IsDeleted && !rp.IsDeleted && !p.IsDeleted
                select p.Code).Distinct().ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);
}
