using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// PO status display labels. The PO DTOs carry the PoStatus enum NAME as a string, so Contracts renders it without seeing
/// the Domain enum: <c>Released</c> reads "Pending", a missing status an em dash, everything else the humanized PascalCase
/// name. Pure and DB-less.
/// </summary>
public class PoStatusLabelsTests
{
    private const string EmDash = "\u2014";

    [Theory]
    [InlineData("Released")]
    [InlineData("released")]
    [InlineData("RELEASED")]
    [InlineData("  Released ")]
    public void Released_reads_as_Pending_whatever_the_case(string status)
        => PoStatusLabels.Label(status).Should().Be("Pending");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_status_reads_as_an_em_dash(string? status)
        => PoStatusLabels.Label(status).Should().Be(EmDash);

    [Theory]
    [InlineData("PartiallyDelivered", "Partially Delivered")]
    [InlineData("FullyShipped", "Fully Shipped")]
    [InlineData("Closed", "Closed")]
    [InlineData("Draft", "Draft")]
    [InlineData("Partially Delivered", "Partially Delivered")]   // already humanized: left alone
    [InlineData("ASNReady", "ASN Ready")]                          // acronym run followed by a word
    public void PascalCase_status_names_are_humanized(string status, string expected)
        => PoStatusLabels.Label(status).Should().Be(expected);

    // Guard against Contracts drifting from the Domain enum: every PoStatus member must produce a readable label, and for
    // all but the Released rename the label may only ADD spacing (no letter changes) to the enum name.
    [Fact]
    public void Every_PoStatus_member_has_a_label_that_only_adds_spacing_except_Released()
    {
        foreach (var name in Enum.GetNames<PoStatus>())
        {
            var label = PoStatusLabels.Label(name);

            if (name == nameof(PoStatus.Released))
            {
                label.Should().Be("Pending");
                continue;
            }

            label.Should().NotBe(EmDash);
            label.Replace(" ", "").Should().Be(name, because: "a status name is humanized, never renamed");
            label.Should().NotContain("  ");
        }
    }
}
