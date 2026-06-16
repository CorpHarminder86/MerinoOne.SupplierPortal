namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface ICurrentUser
{
    string UserCode { get; }
    string? UserName { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> Permissions { get; }
    bool IsAuthenticated { get; }
    bool IsManager { get; }
    bool IsAdmin { get; }
    bool HasPermission(string code);

    /// <summary>
    /// The single tenant this principal belongs to (JWT "tenant" claim). Null for the cross-tenant
    /// Platform Admin and for the anonymous/design-time principal. Basis for the always-on tenant filter.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// True for the cross-tenant Platform Admin (JWT "PlatformAdmin" role). The ONLY tenant-user that
    /// bypasses the tenant filter. Holds no business-data permissions (separation of duties).
    /// </summary>
    bool IsPlatformAdmin { get; }
}
