namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Resolves the current tenant's Infor connection configuration with secrets DECRYPTED, for
/// server-side outbound use. Distinct from the Get query used by the Settings UI, which masks
/// secrets. Returns null when no config row exists for the tenant.
/// </summary>
public interface IInforConnectionProvider
{
    Task<InforConnectionValues?> GetCurrentAsync(CancellationToken ct = default);
}

/// <summary>Decrypted, ready-to-use connection values for the current tenant.</summary>
public record InforConnectionValues(
    string AccessTokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password,
    string ApiBaseUrl,
    string? IonC4wsBaseUrl,
    string Company,
    bool IsActive)
{
    /// <summary>First configured LN company (the Company field is a comma-separated list).</summary>
    public string PrimaryCompany =>
        Company.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessTokenUrl)
        && !string.IsNullOrWhiteSpace(ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(Company);
}
