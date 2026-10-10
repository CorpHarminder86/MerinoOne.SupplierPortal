namespace MerinoOne.SupplierPortal.Application.SystemSettings.Display;

/// <summary>
/// Tenant-wide display preferences — how the portal SHOWS values, never how it stores or transmits them.
/// </summary>
public static class DisplayKeys
{
    public const string Category = "Display";

    /// <summary>
    /// Decimal places shown for prices and money amounts (unit price, line / header amounts, tax and discount
    /// amounts). Integer <c>0..6</c>, default <c>4</c>. Quantities and percentages are NOT governed by it. Feeds the
    /// process-wide <c>AppMoney</c> formatter; stored values and the LN wire are never rounded to it.
    /// </summary>
    public const string MoneyDecimalPlaces = "MoneyDecimalPlaces";
}
