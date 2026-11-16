using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>R19 — invoice line discount / taxable / tax arithmetic (the PO discount is per unit). Pure, DB-less.</summary>
public class InvoiceLineMathTests
{
    [Fact]
    public void Per_unit_discount_is_multiplied_by_billed_qty()
    {
        var discount = InvoiceLineMath.LineDiscount(10m, 8m);
        discount.Should().Be(80m);
        InvoiceLineMath.Taxable(8m, 100m, discount).Should().Be(720m);
    }

    [Fact]
    public void Sample_line_discount_taxable_and_tax()
    {
        var discount = InvoiceLineMath.LineDiscount(50m, 10m);
        discount.Should().Be(500m);
        var taxable = InvoiceLineMath.Taxable(10m, 1000m, discount);
        taxable.Should().Be(9500m);
        InvoiceLineMath.Tax(taxable, 18m).Should().Be(1710m);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void Discount_is_zero_without_a_positive_discount_or_qty(int? perUnit, int qty)
        => InvoiceLineMath.LineDiscount(perUnit, qty).Should().Be(0m);

    [Fact]
    public void PerUnitDiscount_amount_wins_when_positive()
    {
        InvoiceLineMath.PerUnitDiscount(10m, 0m, 250m).Should().Be(10m);
        InvoiceLineMath.PerUnitDiscount(10m, 5m, 250m).Should().Be(10m, because: "both set => the amount wins");
        var discount = InvoiceLineMath.LineDiscount(InvoiceLineMath.PerUnitDiscount(10m, 0m, 250m), 66m);
        InvoiceLineMath.Taxable(66m, 250m, discount).Should().Be(15840m);
    }

    [Fact]
    public void PerUnitDiscount_pct_only_derives_from_unit_price()
    {
        var perUnit = InvoiceLineMath.PerUnitDiscount(0m, 10m, 70m);
        perUnit.Should().Be(7m);
        var discount = InvoiceLineMath.LineDiscount(perUnit, 30m);
        discount.Should().Be(210m);
        InvoiceLineMath.Taxable(30m, 70m, discount).Should().Be(1890m);

        var perUnit2 = InvoiceLineMath.PerUnitDiscount(0m, 5m, 8m);
        perUnit2.Should().Be(0.4m);
        var discount2 = InvoiceLineMath.LineDiscount(perUnit2, 1000m);
        discount2.Should().Be(400m);
        InvoiceLineMath.Taxable(1000m, 8m, discount2).Should().Be(7600m);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(null, 0)]
    [InlineData(-1, -5)]
    public void PerUnitDiscount_is_zero_without_a_positive_amount_or_pct(int? amount, int? pct)
        => InvoiceLineMath.PerUnitDiscount(amount, pct, 100m).Should().Be(0m);

    [Fact]
    public void Taxable_floors_at_zero_when_discount_exceeds_gross()
        => InvoiceLineMath.Taxable(1m, 10m, 25m).Should().Be(0m);

    [Fact]
    public void Tax_is_zero_when_rate_is_null()
        => InvoiceLineMath.Tax(9500m, null).Should().Be(0m);

    [Fact]
    public void Rounding_is_away_from_zero_at_the_midpoint()
    {
        InvoiceLineMath.Tax(0.50m, 1m).Should().Be(0.01m);           // 0.005 -> 0.01 (banker's rounding would give 0.00)
        InvoiceLineMath.LineDiscount(0.125m, 1m).Should().Be(0.13m); // 0.125 -> 0.13
    }
}
