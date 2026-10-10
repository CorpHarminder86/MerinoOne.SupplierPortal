namespace MerinoOne.SupplierPortal.Contracts.SystemSettings;

/// <summary>
/// Wire model for <c>GET api/system-settings/display</c>: the Display-category values every screen needs in order to
/// render. Readable by any authenticated user — unlike the generic settings list, which needs <c>Settings.Read</c> —
/// because a supplier's pages format money too. <see cref="MoneyDecimalPlaces"/> is the effective
/// <c>Display.MoneyDecimalPlaces</c> value (the seeded default when no override is saved), always 0..6.
/// </summary>
public record DisplaySettingsDto(int MoneyDecimalPlaces);
