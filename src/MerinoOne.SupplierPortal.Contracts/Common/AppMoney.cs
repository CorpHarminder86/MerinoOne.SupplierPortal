using System.Globalization;

namespace MerinoOne.SupplierPortal.Contracts.Common;

/// <summary>
/// The portal's single money-display authority: how many decimal places a price or an amount is SHOWN with.
///
/// <para><b>For prices and amounts only</b> — unit prices, line and header amounts, tax amounts, discount amounts.
/// Never quantities and never percentages (tax rate, tolerance %, discount %): those carry their own precision and
/// keep their own formats. This class formats for the screen and nothing else; a stored value and the LN wire are
/// never rounded by it.</para>
///
/// <para><b>Why static rather than an injected service.</b> Same reasoning as <see cref="AppTime"/>: the precision
/// is a property of the deployment, not of the request, and <c>Contracts</c> is the only project both hosts
/// reference, so a static holder is reachable from every <c>.razor</c> page with no <c>@inject</c> line — which is
/// what keeps "handled through a single place" true across all the render sites instead of eroding the first time
/// someone forgets the injection.</para>
///
/// <para><b>Where the value comes from.</b> The <c>Display.MoneyDecimalPlaces</c> system setting. Every process that
/// renders money calls <see cref="Configure"/> once at startup; the API host calls it again whenever that setting is
/// saved or reset. Until then <see cref="Decimals"/> is <see cref="DefaultDecimals"/>.</para>
/// </summary>
public static class AppMoney
{
    /// <summary>Precision in use until <see cref="Configure"/> runs; also the seeded value of the setting.</summary>
    public const int DefaultDecimals = 4;

    /// <summary>Lowest supported precision (whole currency units).</summary>
    public const int MinDecimals = 0;

    /// <summary>Highest supported precision.</summary>
    public const int MaxDecimals = 6;

    /// <summary>What every formatter prints for a null amount. Matches the em-dash the UI already used.</summary>
    public const string Dash = "—";

    // One int read picks the format, so a Configure racing a render can never pair one precision's number with
    // another's. Volatile because Configure runs on a request thread (a settings save) while Blazor circuits on
    // other threads are rendering.
    private static volatile int _decimals = DefaultDecimals;

    // "N0".."N6" and "F0".."F6", indexed by places, so formatting a table cell does not allocate a format string.
    private static readonly string[] GroupedFormats = BuildFormats("N");
    private static readonly string[] PlainFormats = BuildFormats("F");

    /// <summary>Decimal places money is rendered with; always within <see cref="MinDecimals"/>..<see cref="MaxDecimals"/>.</summary>
    public static int Decimals => _decimals;

    /// <summary>
    /// Sets the precision from the Display settings. Call once at startup, from every process that renders money.
    /// Out-of-range input is clamped to 0..6 rather than rejected — a bad stored value must degrade the display,
    /// never take a page (or the host) down.
    /// </summary>
    public static void Configure(int decimals) => _decimals = Math.Clamp(decimals, MinDecimals, MaxDecimals);

    // ---------------- Formatting (the call the UI should reach for) ----------------

    /// <summary>
    /// Digit-grouped, current-culture, <see cref="Decimals"/> places: <c>1,234,567.8912</c>. The replacement for the
    /// scattered <c>ToString("N2")</c> sites.
    /// </summary>
    public static string Format(decimal v) => v.ToString(GroupedFormats[_decimals], CultureInfo.CurrentCulture);

    /// <summary>As <see cref="Format(decimal)"/>; a null amount prints <see cref="Dash"/>.</summary>
    public static string Format(decimal? v) => v is { } value ? Format(value) : Dash;

    /// <summary>
    /// No digit grouping, current-culture decimal separator, <see cref="Decimals"/> places: <c>1234567.8912</c>.
    /// The replacement for the sites that formatted money with a plain <c>0.00</c> rather than <c>N2</c>.
    /// </summary>
    public static string Plain(decimal v) => v.ToString(PlainFormats[_decimals], CultureInfo.CurrentCulture);

    /// <summary>As <see cref="Plain(decimal)"/>; a null amount prints <see cref="Dash"/>.</summary>
    public static string Plain(decimal? v) => v is { } value ? Plain(value) : Dash;

    // ---------------- Internals ----------------

    private static string[] BuildFormats(string specifier)
    {
        var formats = new string[MaxDecimals + 1];
        for (var places = MinDecimals; places <= MaxDecimals; places++)
            formats[places] = specifier + places.ToString(CultureInfo.InvariantCulture);
        return formats;
    }
}
