namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Supplies a valid OAuth2 bearer token for the current tenant's Infor connection, caching it until
/// shortly before expiry. Returns null when the tenant has no (active) configuration or the token
/// request fails — callers treat null as "cannot reach Infor".
/// </summary>
public interface IInforTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// R8 (2026-07-04) — tenant-explicit variant for background workers (the IDM dispatcher has no
    /// <c>ICurrentUser.TenantId</c>). Token is cached per tenant exactly as the current-user path.
    /// </summary>
    Task<string?> GetAccessTokenAsync(Guid tenantId, CancellationToken ct = default);
}
