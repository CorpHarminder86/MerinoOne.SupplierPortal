using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// Per-tenant Infor CloudSuite (ION API) connection configuration. Exactly one live row per
/// tenant (UQ on tenantId, filtered on isDeleted). Holds the OAuth2 password-grant credentials
/// plus the ION API / C4ws base URLs used to obtain a bearer token and call Infor LN.
///
/// Secret columns (<see cref="ClientSecret"/>, <see cref="Password"/>) are stored ENCRYPTED via
/// <c>ISettingProtector</c> (ASP.NET DataProtection) — the plaintext never leaves the API, and the
/// read/Get path masks them with "********" before returning to the client.
/// </summary>
public class InforConnectionSetting : AuditableEntity, ITenantOwned
{
    /// <summary>Owning tenant — connection config is tenant-scoped (one row per tenant).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Infor Mingle SSO OAuth2 token endpoint (e.g. https://mingle-sso.{...}/{tenant}/as/token.oauth2).</summary>
    public string AccessTokenUrl { get; set; } = string.Empty;

    /// <summary>OAuth2 client id (ci) from the Infor Authorized App (.ionapi).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret (cs). Stored encrypted via DataProtection.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Service-account username (saak) for the password grant.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Service-account secret (sask). Stored encrypted via DataProtection.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>ION API base URL (e.g. https://mingle-ionapi.{...}/{tenant}/LN/...).</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>ION C4ws (SOAP) base URL. Optional — only required for C4ws-mode modules.</summary>
    public string? IonC4wsBaseUrl { get; set; }

    /// <summary>Comma-separated LN company numbers (e.g. "2400,7040").</summary>
    public string? Company { get; set; }

    /// <summary>Soft on/off switch without deleting the saved credentials.</summary>
    public bool IsActive { get; set; } = true;
}
