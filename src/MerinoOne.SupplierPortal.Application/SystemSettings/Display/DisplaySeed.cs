using System.Globalization;
using MerinoOne.SupplierPortal.Contracts.Common;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Display;

/// <summary>
/// Declares the Display settings category (keys / defaults / descriptions / validators). Registered as
/// <c>ISettingsCategorySeed</c> so the generic Get/Save/Reset pipeline and the settings UI pick it up with no
/// edits; the typed reader is <see cref="DisplaySettingsService"/>. The range and the default come from
/// <see cref="AppMoney"/> so the validator and the formatter's own clamp can never drift apart.
/// </summary>
public class DisplaySeed : ISettingsCategorySeed
{
    public string Category => DisplayKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DisplayKeys.MoneyDecimalPlaces] = AppMoney.DefaultDecimals.ToString(CultureInfo.InvariantCulture),
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [DisplayKeys.MoneyDecimalPlaces] =
            "Decimal places shown for prices and money amounts (unit price, line and header amounts, tax and " +
            "discount amounts), 0 to 6. Quantities and percentages are not affected, and stored values are never rounded.",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case DisplayKeys.MoneyDecimalPlaces:
                if (!TryParseMoneyDecimalPlaces(value, out _))
                    return $"MoneyDecimalPlaces must be an integer in the range {AppMoney.MinDecimals}..{AppMoney.MaxDecimals}.";
                return null;

            default:
                return $"Unknown Display key '{key}'.";
        }
    }

    /// <summary>
    /// The ONE parse rule for <see cref="DisplayKeys.MoneyDecimalPlaces"/>, shared by <see cref="Validate"/> and
    /// <see cref="DisplaySettingsService"/> so what is accepted on save is exactly what is honoured on read: digits
    /// only (no sign, no whitespace, no decimal point), culture-independent, inside 0..6.
    /// </summary>
    public static bool TryParseMoneyDecimalPlaces(string? raw, out int places)
        => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out places)
           && places is >= AppMoney.MinDecimals and <= AppMoney.MaxDecimals;
}
