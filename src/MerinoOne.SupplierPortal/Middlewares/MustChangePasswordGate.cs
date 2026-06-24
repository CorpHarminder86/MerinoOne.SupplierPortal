using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Models;

namespace MerinoOne.SupplierPortal.Middlewares;

/// <summary>
/// SECURITY — hard server-side gate for forced password change. When the authenticated principal carries the
/// <c>must_change_password=true</c> claim (minted at login for a temp/default-password account — seeded admins,
/// CreateTenantAdmin, admin password reset, the supplier user auto-provisioned on approval), EVERY request is
/// rejected with 403 EXCEPT the change-password endpoint itself (plus session rehydrate/diagnostic). This closes
/// the gap where a temp-password user received a fully-permissioned JWT and could call every authorized API
/// without ever changing the password. The claim clears only when the user changes the password (which clears
/// MustChangePassword) AND re-authenticates to mint a fresh token without the claim — so it fails closed.
/// Must run AFTER UseAuthentication/UseAuthorization so the principal + claims are populated.
/// </summary>
public class MustChangePasswordGate
{
    private readonly RequestDelegate _next;
    public MustChangePasswordGate(RequestDelegate next) => _next = next;

    // The only endpoints reachable while a forced password change is pending.
    private static readonly string[] Allowed =
    {
        "/api/users/me/change-password", // the remediation action
        "/api/auth/me",                  // session rehydrate (client detects the state)
        "/api/auth/whoami",              // identity diagnostic (read-only, harmless)
    };

    public async Task Invoke(HttpContext ctx)
    {
        var principal = ctx.User;
        if (principal?.Identity?.IsAuthenticated == true
            && string.Equals(principal.FindFirst("must_change_password")?.Value, "true", StringComparison.Ordinal))
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            var allowed = Allowed.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/json";
                var body = new Result
                {
                    Success = false,
                    Errors = new List<string>
                    {
                        "Password change required. Change your password (POST /api/users/me/change-password) and sign in again before continuing."
                    },
                    TraceId = ctx.TraceIdentifier
                };
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return;
            }
        }

        await _next(ctx);
    }
}
