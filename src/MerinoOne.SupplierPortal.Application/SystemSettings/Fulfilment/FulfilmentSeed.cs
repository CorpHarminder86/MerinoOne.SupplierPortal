namespace MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;

/// <summary>
/// R4 (2026-06-26) — Decision D3. Declares the Fulfilment settings category (keys / defaults / descriptions /
/// validators). Registered as <c>ISettingsCategorySeed</c> so the generic Get/Save/Reset pipeline and the
/// settings UI pick it up with no edits; the typed reader is <see cref="FulfilmentSettingsService"/>.
/// </summary>
public class FulfilmentSeed : ISettingsCategorySeed
{
    public string Category => FulfilmentKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // D3 — OFF by default (strict zero default is rolled out only after tolerances are seeded).
        [FulfilmentKeys.EnforceOverShipGuard] = "false",
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [FulfilmentKeys.EnforceOverShipGuard] =
            "When ON, ASN ship qty exceeding order qty + over-ship tolerance is REJECTED. The cumulative shipped " +
            "qty is always tracked regardless of this flag; only the ceiling rejection is gated.",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case FulfilmentKeys.EnforceOverShipGuard:
                if (!bool.TryParse(value, out _))
                    return "EnforceOverShipGuard must be 'true' or 'false'.";
                return null;

            default:
                return $"Unknown Fulfilment key '{key}'.";
        }
    }
}
