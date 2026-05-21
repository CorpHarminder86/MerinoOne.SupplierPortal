using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>Used by ef migrations / seed / dev tooling when no HttpContext is present.</summary>
public class AnonymousCurrentUser : ICurrentUser
{
    public string UserCode => "system";
    public string? UserName => "System";
    public IReadOnlyCollection<string> Roles { get; } = new[] { "SuperAdmin" };
    public IReadOnlyCollection<string> Permissions { get; } = Array.Empty<string>();
    public bool IsAuthenticated => true;
    public bool IsManager => true;
    public bool IsAdmin => true;
    public bool HasPermission(string code) => true;
}
