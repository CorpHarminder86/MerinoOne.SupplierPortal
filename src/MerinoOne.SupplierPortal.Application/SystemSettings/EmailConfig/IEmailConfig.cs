namespace MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;

/// <summary>
/// Strongly-typed reader for the EmailConfig category. Values are cached in process and
/// refreshed via <see cref="ISettingsCacheInvalidator"/> after every Save/Reset commit.
/// </summary>
public interface IEmailConfig
{
    string Host { get; }
    int Port { get; }
    bool EnableSsl { get; }
    string UserName { get; }
    /// <summary>Decrypted SMTP password. Empty when no value has been saved or
    /// when the protected blob cannot be unprotected (key-ring rotation safety).</summary>
    string Password { get; }
    bool DefaultCredentials { get; }
    /// <summary>From-address for outbound system mail (settings key <c>Email</c>).</summary>
    string FromAddress { get; }
    /// <summary>True iff <see cref="Host"/> and <see cref="FromAddress"/> are both populated.</summary>
    bool IsConfigured { get; }
}
