namespace MerinoOne.SupplierPortal.Application.SystemSettings;

/// <summary>
/// A category contributor — declares the keys, default values, descriptions and per-key
/// validators for one logical group of settings (e.g. "EmailConfig", "SupplierInvite").
/// New categories drop in by implementing this interface and registering it in DI;
/// no edits to the Get/Save/Reset pipeline are required.
/// </summary>
public interface ISettingsCategorySeed
{
    /// <summary>Stable category name; matches <c>SystemSetting.Category</c>.</summary>
    string Category { get; }

    /// <summary>Key -> default value as stored in the DB seed.</summary>
    IReadOnlyDictionary<string, string> Defaults { get; }

    /// <summary>Key -> description (nullable). Used when synthesising missing rows.</summary>
    IReadOnlyDictionary<string, string?> Descriptions { get; }

    /// <summary>
    /// Per-key validator. Returns <c>null</c> when the value is acceptable, otherwise an
    /// error message that the Save handler surfaces via <c>ValidationException</c>.
    /// Length/required checks happen earlier in FluentValidation.
    /// </summary>
    string? Validate(string key, string value);
}
