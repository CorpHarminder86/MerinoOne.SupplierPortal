using Microsoft.AspNetCore.Authentication;

namespace MerinoOne.SupplierPortal.Identity.ApiKeyAuth;

/// <summary>
/// Options for the inbound X-APIKey authentication scheme. No configurable knobs today; the type
/// exists so the handler can derive from <see cref="AuthenticationHandler{TOptions}"/>.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The scheme name registered in Program.cs. JWT remains the default scheme.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>Request header carrying the plaintext key.</summary>
    public const string HeaderName = "X-APIKey";
}
