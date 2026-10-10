using System.Globalization;
using System.Runtime.Loader;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Common;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// <see cref="AppMoney.Decimals"/> is process-wide, and the test process contains OTHER writers besides this class:
/// the in-process API host configures it at startup, and <c>DisplaySettingsTests</c> drives the Save/Reset hook that
/// re-points it. xUnit runs a <c>DisableParallelization</c> collection only after every parallel collection has
/// finished, so <see cref="AppMoneyTests"/> can never overlap either of them. Any other test that asserts on
/// <see cref="AppMoney"/> output should join this collection for the same reason.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppMoneyStateCollection
{
    public const string Name = "AppMoney process-wide state";
}

/// <summary>
/// The money-display formatter, pinned. A wrong-precision bug does not throw — it shows 12.50 where the order says
/// 12.5000 — so each case here guards a silent-wrongness failure: the clamp (a bad stored value must degrade the
/// display, not crash a page), the grouped/plain split (the two replace different legacy formats), and the null
/// dash. Like <c>AppTimeTests</c>, it restores the static it mutates so a later test never inherits it.
/// </summary>
[Collection(AppMoneyStateCollection.Name)]
public class AppMoneyTests : IDisposable
{
    /// <summary>Six decimals, a value ≥ 1,000,000 (two grouping separators) and a final digit ≥ 5 so every width rounds visibly.</summary>
    private const decimal Sample = 1234567.891256m;

    private readonly int _decimalsOnEntry = AppMoney.Decimals;
    private readonly CultureInfo _cultureOnEntry = CultureInfo.CurrentCulture;

    // Grouping and the decimal mark are culture data, so pin one: the invariant culture reads like en-US and exists
    // on every host. The one test about culture sets its own.
    public AppMoneyTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose()
    {
        AppMoney.Configure(_decimalsOnEntry);
        CultureInfo.CurrentCulture = _cultureOnEntry;
    }

    [Fact]
    public void Starts_at_four_places_before_anything_configures_it()
    {
        // This process cannot show the initial value: the in-process API host has long since called Configure. A
        // collectible AssemblyLoadContext gets its OWN copy of Contracts, and with it an AppMoney nobody has touched.
        var context = new AssemblyLoadContext("AppMoney-pristine", isCollectible: true);
        try
        {
            var contracts = context.LoadFromAssemblyPath(typeof(AppMoney).Assembly.Location);
            var pristine = contracts.GetType(typeof(AppMoney).FullName!, throwOnError: true)!;

            ((int)pristine.GetProperty(nameof(AppMoney.Decimals))!.GetValue(null)!).Should().Be(4);
        }
        finally
        {
            context.Unload();
        }

        AppMoney.DefaultDecimals.Should().Be(4);
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    [InlineData(100, 6)]
    [InlineData(int.MaxValue, 6)]
    public void Configure_clamps_to_zero_through_six(int requested, int expected)
    {
        AppMoney.Configure(requested);

        AppMoney.Decimals.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "1,234,568")]
    [InlineData(1, "1,234,567.9")]
    [InlineData(2, "1,234,567.89")]
    [InlineData(3, "1,234,567.891")]
    [InlineData(4, "1,234,567.8913")]
    [InlineData(5, "1,234,567.89126")]
    [InlineData(6, "1,234,567.891256")]
    public void Format_groups_digits_and_follows_the_configured_places(int places, string expected)
    {
        AppMoney.Configure(places);

        AppMoney.Format(Sample).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "1234568")]
    [InlineData(1, "1234567.9")]
    [InlineData(2, "1234567.89")]
    [InlineData(3, "1234567.891")]
    [InlineData(4, "1234567.8913")]
    [InlineData(5, "1234567.89126")]
    [InlineData(6, "1234567.891256")]
    public void Plain_is_the_same_number_with_no_grouping(int places, string expected)
    {
        AppMoney.Configure(places);

        AppMoney.Plain(Sample).Should().Be(expected);
    }

    [Fact]
    public void Format_and_Plain_differ_only_by_the_grouping_separators()
    {
        AppMoney.Configure(4);

        AppMoney.Format(Sample).Replace(",", "").Should().Be(AppMoney.Plain(Sample));
        AppMoney.Format(Sample).Should().NotBe(AppMoney.Plain(Sample));
    }

    [Fact]
    public void Small_negative_and_zero_amounts_keep_their_places()
    {
        AppMoney.Configure(4);
        AppMoney.Format(0m).Should().Be("0.0000");
        AppMoney.Plain(0m).Should().Be("0.0000");
        AppMoney.Format(-1234.5m).Should().Be("-1,234.5000");
        AppMoney.Plain(-1234.5m).Should().Be("-1234.5000");

        AppMoney.Configure(0);
        AppMoney.Format(0m).Should().Be("0");
        AppMoney.Format(12.5m).Should().Be("13", because: "decimal formatting rounds half away from zero, as the N2 sites it replaces did");
    }

    [Fact]
    public void A_null_amount_renders_the_dash_from_both_formatters()
    {
        AppMoney.Format((decimal?)null).Should().Be("—").And.Be(AppMoney.Dash);
        AppMoney.Plain((decimal?)null).Should().Be("—").And.Be(AppMoney.Dash);
    }

    [Fact]
    public void A_present_nullable_amount_formats_exactly_like_the_plain_decimal()
    {
        AppMoney.Configure(3);

        AppMoney.Format((decimal?)Sample).Should().Be(AppMoney.Format(Sample));
        AppMoney.Plain((decimal?)Sample).Should().Be(AppMoney.Plain(Sample));
        AppMoney.Format((decimal?)0m).Should().Be("0.000", because: "zero is an amount, not a missing one");
    }

    [Fact]
    public void Formatting_uses_the_current_culture()
    {
        AppMoney.Configure(4);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

        AppMoney.Format(Sample).Should().Be("1.234.567,8913");
        AppMoney.Plain(Sample).Should().Be("1234567,8913");
    }
}
