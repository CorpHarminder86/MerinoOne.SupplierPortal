namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>
/// Sentinel shipped to the UI in place of the encrypted ClientSecret / Password. The Save and Test
/// handlers treat an incoming value equal to this string as "keep the stored secret" (no rewrite,
/// and the stored ciphertext is decrypted server-side for the test). Mirrors the EmailConfig pattern.
/// </summary>
public static class InforConnectionSecret
{
    public const string Mask = "********";
}

/// <summary>
/// Current tenant's Infor connection config. <see cref="ClientSecret"/> and <see cref="Password"/>
/// are masked ("********") whenever a value is stored — the ciphertext never leaves the API.
/// </summary>
public record InforConnectionDto(
    string AccessTokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password,
    string ApiBaseUrl,
    string? IonC4wsBaseUrl,
    string? Company,
    bool IsActive,
    bool IsConfigured);

/// <summary>Save (upsert) the current tenant's Infor connection config. Secrets equal to the
/// mask are left untouched server-side.</summary>
public record SaveInforConnectionRequest(
    string AccessTokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password,
    string ApiBaseUrl,
    string? IonC4wsBaseUrl,
    string? Company);

/// <summary>Test the supplied settings by requesting an OAuth2 token. Secrets equal to the mask
/// fall back to the stored (decrypted) values.</summary>
public record TestInforConnectionRequest(
    string AccessTokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password,
    string ApiBaseUrl,
    string? IonC4wsBaseUrl,
    string? Company);

/// <summary>Structured result of a connection test — surfaced verbatim in the Settings UI banner.</summary>
public record InforConnectionTestResult(bool Success, string Message);
