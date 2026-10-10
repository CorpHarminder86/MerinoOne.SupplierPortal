namespace MerinoOne.SupplierPortal.Application.SystemSettings.Display;

/// <summary>Strongly-typed reader for the Display category.</summary>
public interface IDisplaySettings
{
    /// <summary>
    /// Decimal places for prices and money amounts, 0..6. Defaults to 4 when unset or invalid (including when the
    /// settings table cannot be read).
    /// </summary>
    Task<int> GetMoneyDecimalPlacesAsync(CancellationToken ct);
}
