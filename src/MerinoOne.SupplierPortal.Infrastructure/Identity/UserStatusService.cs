using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// Cache-backed active-account check consulted once per request in the JWT OnTokenValidated event.
/// A deactivated user is rejected on their next request (immediate lockout) rather than lingering
/// until token expiry. Short TTL backstop plus explicit invalidation on activate/deactivate.
/// </summary>
public sealed class UserStatusService : IUserStatusService
{
    private readonly IAppDbContext _db;
    private readonly IDistributedCache _cache;

    private static readonly DistributedCacheEntryOptions Ttl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
    };

    public UserStatusService(IAppDbContext db, IDistributedCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string Key(Guid userId) => $"active:{userId:N}";

    public async Task<bool> IsActiveAsync(Guid userId, CancellationToken ct)
    {
        var cached = await _cache.GetStringAsync(Key(userId), ct);
        if (cached is not null) return cached == "1";

        var active = await _db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => u.IsActive && !u.IsDeleted)
            .FirstOrDefaultAsync(ct);

        await _cache.SetStringAsync(Key(userId), active ? "1" : "0", Ttl, ct);
        return active;
    }

    public Task InvalidateAsync(Guid userId, CancellationToken ct) => _cache.RemoveAsync(Key(userId), ct);
}
