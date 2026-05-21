using System.Security.Claims;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Identity;

public class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public string UserCode => Principal?.FindFirst("userCode")?.Value
                              ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                              ?? string.Empty;

    public string? UserName => Principal?.Identity?.Name;

    public IReadOnlyCollection<string> Roles =>
        Principal?.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToArray() ?? Array.Empty<string>();

    public IReadOnlyCollection<string> Permissions =>
        Principal?.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray() ?? Array.Empty<string>();

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool IsManager => Roles.Contains("Buyer") || Roles.Contains("Finance");
    public bool IsAdmin => Roles.Contains("SuperAdmin") || Roles.Contains("Admin");
    public bool HasPermission(string code) => Permissions.Contains(code);
}
