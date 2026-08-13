using System.Globalization;
using FluentAssertions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// Mirrors <c>PurchaseOrderDetail.OnDeliveryChanged</c> — the parse behind the negotiation delivery
/// <c>&lt;input type="datetime-local"&gt;</c>.
///
/// <para><b>Why this parse is hand-written rather than <c>@bind:format</c>.</b> The control originally used
/// <c>@bind</c> with <c>@bind:format="yyyy-MM-ddTHH:mm"</c>. In the running app that silently failed to parse
/// the browser's value: the model never updated, Blazor re-rendered the old value, and the field snapped back —
/// which users reported as "I cannot change the delivery date". Reproduced in-browser on 2026-08-13, where the
/// Qty input on the SAME row bound correctly, isolating it to the date conversion rather than component state.
/// Adding <c>@bind:culture</c> did not help. Parsing explicitly makes the behaviour ours, culture-proof, and
/// testable — none of which was true while it lived inside the directive.</para>
///
/// <para>Note the asymmetry that makes this class worth keeping: a failed parse produces NO error anywhere. It
/// is indistinguishable from a read-only control, so only a test like this can catch a regression.</para>
/// </summary>
public class DateTimeLocalBindFormatTests
{
    /// <summary>Must stay in step with <c>PurchaseOrderDetail.DeliveryInputFormats</c>.</summary>
    private static readonly string[] Formats = { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss" };

    private static bool TryParse(string raw, out DateTime value) =>
        DateTime.TryParseExact(raw, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    [Theory]
    [InlineData("2026-08-20T09:30", 2026, 8, 20, 9, 30, 0)]   // default step: minute precision
    [InlineData("2026-08-20T09:30:45", 2026, 8, 20, 9, 30, 45)] // finer step: seconds included
    [InlineData("2026-08-12T15:34", 2026, 8, 12, 15, 34, 0)]   // the seeded value on a real PO
    [InlineData("2026-01-01T00:00", 2026, 1, 1, 0, 0, 0)]      // midnight
    public void Accepts_every_shape_a_browser_posts(string raw, int y, int mo, int d, int h, int mi, int s)
    {
        TryParse(raw, out var value).Should().BeTrue(because: $"'{raw}' is a value the control can post");
        value.Should().Be(new DateTime(y, mo, d, h, mi, s));
    }

    [Fact]
    public void Renders_the_value_in_the_shape_the_control_requires()
    {
        // The round trip: what the field renders must be re-readable, or editing breaks after the first change.
        var rendered = new DateTime(2026, 8, 20, 9, 30, 0).ToString(Formats[0], CultureInfo.InvariantCulture);

        rendered.Should().Be("2026-08-20T09:30");
        TryParse(rendered, out _).Should().BeTrue();
    }

    [Fact]
    public void Parsing_is_culture_independent()
    {
        // Blazor's own binding parses with CurrentCulture. OnDeliveryChanged pins InvariantCulture precisely so a
        // server whose locale formats dates differently cannot make the picker stop accepting input.
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "en-IN", "fr-FR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                TryParse("2026-08-20T09:30", out var value).Should().BeTrue(because: $"culture {name} must not matter");
                value.Should().Be(new DateTime(2026, 8, 20, 9, 30, 0));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Rejects_values_the_handler_must_ignore_rather_than_treat_as_a_clear()
    {
        // OnDeliveryChanged leaves the model untouched on an unparseable value. Blanking it instead would
        // silently drop a date the supplier still believes is set.
        TryParse("2026-08-20", out _).Should().BeFalse();
        TryParse("20/08/2026 09:30", out _).Should().BeFalse();
        TryParse("2026-08-20T09:30:00.0000000", out _).Should().BeFalse();
    }
}
