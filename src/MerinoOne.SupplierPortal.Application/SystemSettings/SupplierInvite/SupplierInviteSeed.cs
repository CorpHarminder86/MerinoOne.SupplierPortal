namespace MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;

public class SupplierInviteSeed : ISettingsCategorySeed
{
    public string Category => SupplierInviteKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SupplierInviteKeys.ExpiryDays] = "7",
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [SupplierInviteKeys.ExpiryDays] = "Days before a supplier invite token expires.",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case SupplierInviteKeys.ExpiryDays:
                if (!int.TryParse(value, out var d) || d < 1 || d > 365)
                    return "ExpiryDays must be an integer in the range 1..365.";
                return null;

            default:
                return $"Unknown SupplierInvite key '{key}'.";
        }
    }
}
