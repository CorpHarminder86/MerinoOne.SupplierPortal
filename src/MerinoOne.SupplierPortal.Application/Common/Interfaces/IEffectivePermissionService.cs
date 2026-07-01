namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Resolves a user's CURRENT effective permission codes, cached with a short TTL and invalidated
/// on every role/permission mutation. Backs the per-request permission-claim enrichment so an admin's
/// grant/revoke takes effect on the user's next request — with no relogin.
/// </summary>
public interface IEffectivePermissionService
{
    Task<IReadOnlySet<string>> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>Evict the cached permission set for the given users (call after any role/permission change).</summary>
    Task InvalidateAsync(IEnumerable<Guid> userIds, CancellationToken ct);
}
