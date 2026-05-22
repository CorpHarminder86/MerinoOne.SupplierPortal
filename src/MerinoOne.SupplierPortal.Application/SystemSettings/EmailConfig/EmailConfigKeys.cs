namespace MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;

/// <summary>String constants for the seven EmailConfig setting keys — kept in one place so
/// the seed, the service reader and validators all reference the same identifiers.</summary>
public static class EmailConfigKeys
{
    public const string Category = "EmailConfig";

    public const string Email = "Email";
    public const string Host = "Host";
    public const string Port = "Port";
    public const string EnableSsl = "EnableSsl";
    public const string UserName = "UserName";
    public const string Password = "Password";
    public const string DefaultCredentials = "DefaultCredentials";

    /// <summary>Sentinel value shipped to the UI in place of the encrypted password.
    /// Save handler must skip the write when the incoming value equals this string.</summary>
    public const string PasswordMask = "********";
}
