using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Common;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// The portal's UTC-in-store / local-on-screen rule, pinned.
///
/// <para>Every case here guards a silent-wrongness failure: a date/time bug does not throw, it just shows a
/// number that is 5½ hours off, and nobody notices until a delivery is booked for the wrong shift. The two that
/// matter most are the <c>Unspecified</c> handling (SQL Server and System.Text.Json both hand back
/// <c>Unspecified</c> for a value with no offset — treating that as local would double-convert) and the
/// round-trip, which is what makes a picker safe to bind.</para>
///
/// <para>These tests set the process-wide display zone. That is safe today because <see cref="AppTime"/> is read
/// for ASSERTED output in no other test — server-side its only consumer is <c>PoNegotiationHistory</c>, whose
/// audit strings nothing asserts on. If that changes, this class needs its own non-parallel collection.</para>
/// </summary>
public class AppTimeTests : IDisposable
{
    private const string IndiaZoneId = "India Standard Time";

    /// <summary>2026-08-12 10:04 UTC — a real inbound <c>poDate</c> from merino-supplier-dev (PUR000355).</summary>
    private static readonly DateTime PoDateUtc = new(2026, 8, 12, 10, 4, 0, DateTimeKind.Unspecified);

    /// <summary>The same instant as an Indian wall clock.</summary>
    private static readonly DateTime PoDateIst = new(2026, 8, 12, 15, 34, 0);

    public AppTimeTests() => AppTime.Configure(IndiaZoneId);

    // Return the process to its configured default so a later test never inherits this class's zone.
    public void Dispose() => AppTime.Configure(null);

    [Fact]
    public void ToLocal_treats_an_Unspecified_value_as_UTC()
    {
        // The regression this whole design exists to prevent: SQL and JSON return Unspecified, the store is UTC,
        // so the kind must be STAMPED. Calling ToUniversalTime() here instead would shift by the host offset and
        // land 5:30 short on an IST host — and be perfectly correct on a UTC host, which is how it survives review.
        AppTime.ToLocal(PoDateUtc).Should().Be(PoDateIst);
    }

    [Fact]
    public void ToLocal_handles_an_explicitly_Utc_value_identically()
        => AppTime.ToLocal(DateTime.SpecifyKind(PoDateUtc, DateTimeKind.Utc)).Should().Be(PoDateIst);

    [Fact]
    public void ToLocal_normalises_a_Local_kind_value_through_UTC()
    {
        // Built from the same instant, so the expectation holds whatever zone the test host runs in.
        var asLocalKind = DateTime.SpecifyKind(PoDateUtc, DateTimeKind.Utc).ToLocalTime();
        AppTime.ToLocal(asLocalKind).Should().Be(PoDateIst);
    }

    [Fact]
    public void ToUtc_converts_a_picker_wall_clock_value_back()
        => AppTime.ToUtc(PoDateIst).Should().Be(DateTime.SpecifyKind(PoDateUtc, DateTimeKind.Utc));

    [Theory]
    [InlineData(2026, 8, 12, 10, 4)]    // mid-morning UTC
    [InlineData(2026, 8, 11, 20, 0)]    // crosses the date line into the next IST day
    [InlineData(2026, 1, 1, 0, 0)]      // midnight, new year
    [InlineData(2026, 12, 29, 11, 0)]   // a real PO line delivery date
    public void ToUtc_inverts_ToLocal_exactly(int y, int mo, int d, int h, int mi)
    {
        var utc = new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified);

        // The property a bound picker depends on: seed it via ToLocal, read it back via ToUtc, get the same
        // instant. Without this an untouched line would re-submit a shifted date on every save.
        AppTime.ToUtc(AppTime.ToLocal(utc)).Should().Be(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    [Fact]
    public void ToLocal_moves_the_DATE_when_the_offset_crosses_midnight()
    {
        // 20:00 UTC on the 11th is 01:30 IST on the 12th. A formatter that showed the raw stored value would name
        // the wrong day, not merely the wrong hour.
        var utc = new DateTime(2026, 8, 11, 20, 0, 0, DateTimeKind.Unspecified);
        AppTime.Date(utc).Should().Be("2026-08-12");
        AppTime.Stamp(utc).Should().Be("2026-08-12 01:30 AM");
    }

    [Fact]
    public void Formatters_render_in_the_display_zone()
    {
        AppTime.Date(PoDateUtc).Should().Be("2026-08-12");
        AppTime.Stamp(PoDateUtc).Should().Be("2026-08-12 03:34 PM");
        AppTime.StampSeconds(PoDateUtc).Should().Be("2026-08-12 03:34:00 PM");

        // An explicit format still wins — this is the shape the datetime-local picker requires, and it must
        // stay 24-hour even though everything the user READS is now 12-hour.
        AppTime.Format(PoDateUtc, "yyyy-MM-dd'T'HH:mm").Should().Be("2026-08-12T15:34");
    }

    [Theory]
    [InlineData(2026, 8, 12, 10, 4, "2026-08-12 03:34 PM")]   // afternoon
    [InlineData(2026, 8, 11, 20, 0, "2026-08-12 01:30 AM")]   // after midnight, next IST day
    [InlineData(2026, 8, 12, 6, 30, "2026-08-12 12:00 PM")]   // exactly noon
    [InlineData(2026, 8, 11, 18, 30, "2026-08-12 12:00 AM")]  // exactly midnight
    public void Stamp_uses_12_hour_with_an_AM_PM_designator(int y, int mo, int d, int h, int mi, string expected)
    {
        // Noon and midnight are the two values a 12-hour format gets wrong when written with the wrong
        // specifier ("HH" would print 12/00, "hh" prints 12 for both and the designator disambiguates).
        AppTime.Stamp(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified)).Should().Be(expected);
    }

    [Fact]
    public void Null_renders_the_dash_by_default_and_honours_an_override()
    {
        AppTime.Date(null).Should().Be(AppTime.Dash);
        AppTime.Stamp(null).Should().Be(AppTime.Dash);
        AppTime.StampSeconds(null).Should().Be(AppTime.Dash);
        AppTime.Stamp(null, "Never").Should().Be("Never");
    }

    [Fact]
    public void Nullable_conversions_pass_null_through()
    {
        AppTime.ToLocal((DateTime?)null).Should().BeNull();
        AppTime.ToUtc((DateTime?)null).Should().BeNull();
    }

    [Fact]
    public void OffsetLabel_names_the_zone_offset()
        => AppTime.OffsetLabel.Should().Be("UTC+05:30");

    // The two fallback tests below each PROVE Configure actually moved the zone before asserting where it
    // landed. Asserting only "Zone == TimeZoneInfo.Local" would pass vacuously on a host whose local zone is
    // already the one under test — which is exactly the host these run on.

    [Fact]
    public void An_unknown_zone_id_falls_back_instead_of_throwing()
    {
        AppTime.Configure("UTC");
        AppTime.OffsetLabel.Should().Be("UTC+00:00", because: "the arrangement must be observable, or the assertion below proves nothing");

        // A typo in appsettings must not crash-loop the host on boot: a clock that is wrong by an offset is
        // recoverable, a process that will not start is not.
        var act = () => AppTime.Configure("Not/A/Real/Zone");

        act.Should().NotThrow();
        AppTime.Zone.Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public void A_blank_zone_id_uses_the_host_zone()
    {
        AppTime.Configure("UTC");
        AppTime.OffsetLabel.Should().Be("UTC+00:00");

        AppTime.Configure("   ");
        AppTime.Zone.Should().Be(TimeZoneInfo.Local);
    }
}
