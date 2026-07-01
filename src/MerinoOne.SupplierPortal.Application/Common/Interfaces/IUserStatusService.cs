namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Per-request active-account check, cached with a short TTL and invalidated when a user is
/// deactivated/reactivated. Backs immediate lockout: a deactivated user is rejected on their next
/// request rather than lingering until token expiry.
/// </summary>
public interface IUserStatusService
{
    Task<bool> IsActiveAsync(Guid userId, CancellationToken ct);

    /// <summary>Evict the cached status for a user (call after activate/deactivate).</summary>
    Task InvalidateAsync(Guid userId, CancellationToken ct);
}
