using System.Text.RegularExpressions;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;

/// <summary>
/// Declares the seven EmailConfig keys, their seed defaults (matching the migration), and the
/// per-key validators that run during Save. Mirrors the DB seed so a freshly-rebuilt context
/// can re-create missing rows on demand without a migration round-trip.
/// </summary>
public class EmailConfigSeed : ISettingsCategorySeed
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Category => EmailConfigKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EmailConfigKeys.Email] = "",
        [EmailConfigKeys.Host] = "",
        [EmailConfigKeys.Port] = "587",
        [EmailConfigKeys.EnableSsl] = "true",
        [EmailConfigKeys.UserName] = "",
        [EmailConfigKeys.Password] = "",
        [EmailConfigKeys.DefaultCredentials] = "true",
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [EmailConfigKeys.Email] = "From-address for outbound system mail.",
        [EmailConfigKeys.Host] = "SMTP server hostname (e.g. smtp.office365.com).",
        [EmailConfigKeys.Port] = "SMTP port. 587 (STARTTLS) or 465 (SSL).",
        [EmailConfigKeys.EnableSsl] = "Use TLS/SSL when connecting to SMTP server.",
        [EmailConfigKeys.UserName] = "SMTP username when DefaultCredentials=false.",
        [EmailConfigKeys.Password] = "SMTP password (encrypted via DataProtection).",
        [EmailConfigKeys.DefaultCredentials] = "Use network default credentials instead of UserName/Password.",
    };

    public string? Validate(string key, string value)
    {
        // Empty values are allowed for most keys (admin clears a field) — only the parser-style
        // checks reject empty/malformed input. The UserName-required-when-explicit-creds rule is
        // cross-field and runs in the Save handler.
        switch (key)
        {
            case EmailConfigKeys.Email:
                if (string.IsNullOrWhiteSpace(value)) return null;
                return EmailRegex.IsMatch(value)
                    ? null
                    : "Email must be a valid email address.";

            case EmailConfigKeys.Host:
                // Permit empty so an admin can clear the field; the consumer treats blank Host as "not configured".
                return null;

            case EmailConfigKeys.Port:
                if (!int.TryParse(value, out var p) || p < 1 || p > 65535)
                    return "Port must be an integer in the range 1..65535";
                return null;

            case EmailConfigKeys.EnableSsl:
            case EmailConfigKeys.DefaultCredentials:
                return bool.TryParse(value, out _) ? null : $"{key} must be 'true' or 'false'.";

            case EmailConfigKeys.UserName:
                return value.Length <= 500 ? null : "UserName must be 500 characters or fewer.";

            case EmailConfigKeys.Password:
                return value.Length <= 500 ? null : "Password must be 500 characters or fewer.";

            default:
                return $"Unknown EmailConfig key '{key}'.";
        }
    }
}
