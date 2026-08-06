using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Issues OAuth2 bearer tokens for the current tenant's Infor connection, cached in
/// <see cref="IMemoryCache"/> until ~60s before expiry. Keyed by tenant so a multi-tenant host never
/// hands one tenant's token to another. Returns null when the tenant is unconfigured/inactive or the
/// token request fails (callers treat null as "cannot reach Infor").
/// </summary>
public class InforTokenProvider : IInforTokenProvider
{
    private readonly IInforConnectionProvider _connections;
    private readonly InforOAuthTokenClient _oauth;
    private readonly ICurrentUser _user;
    private readonly IMemoryCache _cache;
    private readonly ILogger<InforTokenProvider> _logger;

    public InforTokenProvider(
        IInforConnectionProvider connections,
        InforOAuthTokenClient oauth,
        ICurrentUser user,
        IMemoryCache cache,
        ILogger<InforTokenProvider> logger)
    {
        _connections = connections;
        _oauth = oauth;
        _user = user;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var conn = await _connections.GetCurrentAsync(ct);
        return await IssueAsync(conn, _user.TenantId, ct);
    }

    // R8 (2026-07-04) — tenant-explicit variant for background workers (no ICurrentUser context).
    public async Task<string?> GetAccessTokenAsync(Guid tenantId, CancellationToken ct = default)
    {
        var conn = await _connections.GetForTenantAsync(tenantId, ct);
        return await IssueAsync(conn, tenantId, ct);
    }

    // Single-flight guard per tenant (2026-08-06). The outbox dispatcher drains rows CONCURRENTLY, so a
    // cold cache meant N simultaneous password-grant requests — and Infor Mingle SSO rejects the losers of
    // that race (observed twice: the first of a pair of same-second dispatches failed token acquisition,
    // the second succeeded). Static because the provider is resolved per-scope while the race is
    // process-wide; keyed per tenant so one tenant's token fetch never serialises another's.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> TokenLocks = new();

    private async Task<string?> IssueAsync(Application.Common.Interfaces.InforConnectionValues? conn, Guid? tenantId, CancellationToken ct)
    {
        if (conn is null || !conn.IsActive || !conn.IsConfigured)
            return null;

        var cacheKey = $"infor-token:{tenantId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        var gate = TokenLocks.GetOrAdd(tenantId ?? Guid.Empty, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Double-check under the lock: the winner of the race has usually just cached a token.
            if (_cache.TryGetValue(cacheKey, out cached) && !string.IsNullOrEmpty(cached))
                return cached;

            var result = await _oauth.RequestAsync(
                conn.AccessTokenUrl, conn.ClientId, conn.ClientSecret, conn.Username, conn.Password, ct);

            if (!result.Success || string.IsNullOrEmpty(result.AccessToken))
            {
                _logger.LogWarning("Infor token acquisition failed for tenant {TenantId}: {Message}", tenantId, result.Message);
                return null;
            }

            // 60s safety buffer; floor at 30s so a tiny expires_in never yields a zero/negative TTL.
            var ttl = TimeSpan.FromSeconds(Math.Max(30, (result.ExpiresInSeconds ?? 3600) - 60));
            _cache.Set(cacheKey, result.AccessToken, ttl);
            return result.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}
