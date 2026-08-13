using System.Globalization;

namespace MerinoOne.SupplierPortal.Contracts.Common;

/// <summary>
/// The portal's single date/time authority.
///
/// <para><b>The rule this class exists to enforce:</b> every <c>datetime2</c> column, every DTO field and every
/// value on the LN wire is <b>UTC</b>. The only place a local wall-clock time exists is on screen and inside a
/// date/time picker. Nothing outside this class may convert between the two.</para>
///
/// <para>Verified against <c>merino-supplier-dev</c> 2026-08-12: on real (non-seeded) purchase orders the
/// inbound <c>poDate</c> lands 1–9 minutes BEFORE <c>createdOn</c>, and <c>createdOn</c> is written from
/// <see cref="DateTime.UtcNow"/>. LN therefore already sends UTC and the stored convention is genuine — this
/// class converts for display only, it does not correct a skew.</para>
///
/// <para><b>Why static rather than an injected service.</b> The display zone is a property of the deployment,
/// not of the request: the portal serves one organisation from one host, and both processes (Blazor Server and
/// the API) render or stamp text for the same audience. A static holder configured once at startup is reachable
/// from a <c>.razor</c> file with no <c>@inject</c> line, which is what makes "handled through a single place"
/// hold across ~90 render sites instead of eroding the first time someone forgets the injection. Per-user zones
/// would need this to become scoped — see <see cref="Configure"/>.</para>
/// </summary>
public static class AppTime
{
    /// <summary>Configuration key holding the display zone id (Windows or IANA). Empty ⇒ the host's own zone.</summary>
    public const string ConfigKey = "App:TimeZone";

    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// 12-hour display with an AM/PM designator (2026-08-12 03:34 PM).
    ///
    /// <para><b>Display only.</b> Two other formats in the codebase look similar and must stay 24-hour, because
    /// they are wire formats rather than something a person reads: <c>PoLineDocumentAssembler.FormatUtc</c> (the
    /// LN payload) and <c>PurchaseOrderDetail.DeliveryInputFormats</c> (an <c>&lt;input type="datetime-local"&gt;</c>
    /// only accepts ISO 24-hour, whatever the browser chooses to SHOW the user). Changing either to 12-hour
    /// breaks the integration or the picker.</para>
    ///
    /// <para><c>hh</c> rather than <c>h</c>: these values sit in tabular, tabular-nums columns, and a
    /// single-digit hour would ragged the alignment.</para>
    /// </summary>
    public const string StampFormat = "yyyy-MM-dd hh:mm tt";

    /// <inheritdoc cref="StampFormat"/>
    public const string StampSecondsFormat = "yyyy-MM-dd hh:mm:ss tt";

    /// <summary>What every formatter prints for a null instant. Matches the em-dash the UI already used.</summary>
    public const string Dash = "—";

    private static TimeZoneInfo _zone = TimeZoneInfo.Local;

    /// <summary>The zone all display values are rendered in. Never used to interpret a stored value.</summary>
    public static TimeZoneInfo Zone => _zone;

    /// <summary>
    /// Sets the display zone from configuration. Call once at startup, from every process that renders or
    /// stamps a user-facing time. An unknown or blank id falls back to <see cref="TimeZoneInfo.Local"/> — on
    /// WEBAPP01 that is already IST, so the portal is correct with no configuration at all.
    /// </summary>
    public static void Configure(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            _zone = TimeZoneInfo.Local;
            return;
        }

        // .NET resolves both Windows ("India Standard Time") and IANA ("Asia/Kolkata") ids on either OS, but a
        // typo in appsettings must not take the process down — a wrong-by-5:30 clock is recoverable, a crash-loop
        // on boot is not.
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _zone = TimeZoneInfo.Local;
        }
    }

    /// <summary>The current instant, UTC. Use in place of <see cref="DateTime.UtcNow"/> at call sites that also
    /// deal in local values, so the pairing reads honestly.</summary>
    public static DateTime UtcNow => DateTime.UtcNow;

    /// <summary>The current instant as a wall-clock time in <see cref="Zone"/>. For seeding pickers only.</summary>
    public static DateTime Now => ToLocal(DateTime.UtcNow);

    /// <summary>e.g. <c>UTC+05:30</c>. For column headers and tooltips that must name the zone being shown.</summary>
    public static string OffsetLabel
    {
        get
        {
            var offset = _zone.GetUtcOffset(DateTime.UtcNow);
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            return string.Create(CultureInfo.InvariantCulture,
                $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}");
        }
    }

    // ---------------- Conversion ----------------

    /// <summary>
    /// Stored/transport UTC → wall-clock time in <see cref="Zone"/>, for display or for binding an
    /// <c>&lt;input type="datetime-local"&gt;</c>.
    /// </summary>
    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), _zone);

    /// <inheritdoc cref="ToLocal(DateTime)"/>
    public static DateTime? ToLocal(DateTime? utc) => utc is { } v ? ToLocal(v) : null;

    /// <summary>
    /// Wall-clock time in <see cref="Zone"/> (what a picker hands back) → UTC, for persistence and for the wire.
    /// Apply this to EVERY user-entered instant before it leaves the page.
    /// </summary>
    public static DateTime ToUtc(DateTime local)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A zone with DST has wall-clock times that never happened (the spring-forward gap); ConvertTimeToUtc
        // throws on those. India has no DST so this cannot fire today, but the zone is configurable — resolve
        // the gap by the zone's own offset rather than propagating an exception out of a save handler.
        if (_zone.IsInvalidTime(wall))
            return wall - _zone.GetUtcOffset(wall);

        return TimeZoneInfo.ConvertTimeToUtc(wall, _zone);
    }

    /// <inheritdoc cref="ToUtc(DateTime)"/>
    public static DateTime? ToUtc(DateTime? local) => local is { } v ? ToUtc(v) : null;

    // ---------------- Formatting (the call the UI should reach for) ----------------

    /// <summary>Date only, in <see cref="Zone"/>: <c>2026-08-12</c>.</summary>
    public static string Date(DateTime? utc, string fallback = Dash) => Format(utc, DateFormat, fallback);

    /// <inheritdoc cref="Date(DateTime?, string)"/>
    public static string Date(DateTime utc) => Format(utc, DateFormat);

    /// <summary>Date and time to the minute, in <see cref="Zone"/>: <c>2026-08-12 15:34</c>.</summary>
    public static string Stamp(DateTime? utc, string fallback = Dash) => Format(utc, StampFormat, fallback);

    /// <inheritdoc cref="Stamp(DateTime?, string)"/>
    public static string Stamp(DateTime utc) => Format(utc, StampFormat);

    /// <summary>Date and time to the second, in <see cref="Zone"/>.</summary>
    public static string StampSeconds(DateTime? utc, string fallback = Dash) => Format(utc, StampSecondsFormat, fallback);

    /// <inheritdoc cref="StampSeconds(DateTime?, string)"/>
    public static string StampSeconds(DateTime utc) => Format(utc, StampSecondsFormat);

    /// <summary>Arbitrary format, applied to the value AFTER conversion into <see cref="Zone"/>.</summary>
    public static string Format(DateTime? utc, string format, string fallback = Dash) => utc is { } v
        ? ToLocal(v).ToString(format, CultureInfo.InvariantCulture)
        : fallback;

    /// <inheritdoc cref="Format(DateTime?, string, string)"/>
    public static string Format(DateTime utc, string format) => ToLocal(utc).ToString(format, CultureInfo.InvariantCulture);

    // ---------------- Internals ----------------

    /// <summary>
    /// Normalises an inbound value to a genuine UTC instant. SQL Server and System.Text.Json both hand back
    /// <see cref="DateTimeKind.Unspecified"/> for a value with no offset, and the store is UTC by convention, so
    /// Unspecified is stamped rather than shifted — <c>ToUniversalTime()</c> here would silently apply the
    /// host's offset and double-convert.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
