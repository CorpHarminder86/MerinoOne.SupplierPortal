using MerinoOne.SupplierPortal.Application.Shipments.Policies;

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
        // R4 (2026-06-30) — no rounding by default (rollout-safe; ops switch to Floor per-tenant after validation).
        [FulfilmentKeys.OverShipAllowanceRounding] = nameof(OverShipRoundingMode.None),
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [FulfilmentKeys.EnforceOverShipGuard] =
            "When ON, ASN ship qty exceeding order qty + over-ship tolerance is REJECTED. The cumulative shipped " +
            "qty is always tracked regardless of this flag; only the ceiling rejection is gated.",
        [FulfilmentKeys.OverShipAllowanceRounding] =
            "Rounds the over-ship allowance to a whole unit (for discrete UOMs). None = exact (e.g. 2.3). " +
            "Floor = round down (2.3 → 2) — safe, never grants more than the configured tolerance. " +
            "Round = nearest (2.5 → 3). Ceiling = round up (2.3 → 3) — GRANTS MORE than the configured tolerance.",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case FulfilmentKeys.EnforceOverShipGuard:
                if (!bool.TryParse(value, out _))
                    return "EnforceOverShipGuard must be 'true' or 'false'.";
                return null;

            case FulfilmentKeys.OverShipAllowanceRounding:
                if (!Enum.TryParse<OverShipRoundingMode>(value, ignoreCase: true, out _))
                    return "OverShipAllowanceRounding must be one of None, Floor, Round, Ceiling.";
                return null;

            default:
                return $"Unknown Fulfilment key '{key}'.";
        }
    }
}
