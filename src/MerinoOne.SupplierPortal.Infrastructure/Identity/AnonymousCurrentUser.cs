using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// Used by ef migrations / seed / dev tooling / background workers when no HttpContext is present.
/// Implements <see cref="ISystemPrincipal"/> so it bypasses BOTH the tenant and company filters —
/// seeders and workers stamp scope explicitly on the rows they write.
/// </summary>
public class AnonymousCurrentUser : ICurrentUser, ISystemPrincipal
{
    public string UserCode => "system";
    public string? UserName => "System";
    public IReadOnlyCollection<string> Roles { get; } = new[] { "SuperAdmin" };
    public IReadOnlyCollection<string> Permissions { get; } = Array.Empty<string>();
    public bool IsAuthenticated => true;
    public bool IsManager => true;
    public bool IsAdmin => true;
    public bool HasPermission(string code) => true;

    // System principal: no fixed tenant; bypasses the tenant filter via ISystemPrincipal (not IsPlatformAdmin).
    public Guid? TenantId => null;
    public bool IsPlatformAdmin => false;
}
