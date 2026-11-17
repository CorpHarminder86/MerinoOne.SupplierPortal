using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Common;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>R19 — the invoice PDF's amount in words (Indian numbering). Pure, DB-less.</summary>
public class AmountInWordsTests
{
    [Theory]
    [InlineData(16840, "INR", "Indian Rupees Sixteen Thousand Eight Hundred Forty Only")]
    [InlineData(0, "INR", "Indian Rupees Zero Only")]
    [InlineData(10000000, "INR", "Indian Rupees One Crore Only")]
    [InlineData(1234567, "INR", "Indian Rupees Twelve Lakh Thirty Four Thousand Five Hundred Sixty Seven Only")]
    [InlineData(1000000000, "INR", "Indian Rupees One Hundred Crore Only")]
    [InlineData(115, "INR", "Indian Rupees One Hundred Fifteen Only")]
    [InlineData(-10, "INR", "Indian Rupees Minus Ten Only")]
    public void Whole_amounts_use_indian_numbering(int amount, string currency, string expected)
        => AmountInWords.Format(amount, currency).Should().Be(expected);

    [Fact]
    public void Paise_are_spelled_out_for_inr()
        => AmountInWords.Format(123.50m, "INR")
            .Should().Be("Indian Rupees One Hundred Twenty Three and Fifty Paise Only");

    [Fact]
    public void Fraction_that_rounds_to_a_whole_rupee_carries()
        => AmountInWords.Format(99.999m, "INR").Should().Be("Indian Rupees One Hundred Only");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_currency_means_inr(string? currency)
        => AmountInWords.Format(16840m, currency).Should().Be("Indian Rupees Sixteen Thousand Eight Hundred Forty Only");

    // 2026-11-17 feedback — the reported invoice: EUR 848.45 printed "EUR Eight Hundred Forty Eight and 45/100 Only".
    [Theory]
    [InlineData(848.45, "EUR", "Euro Eight Hundred Forty Eight and Forty Five Cents Only")]
    [InlineData(10.25, "USD", "US Dollars Ten and Twenty Five Cents Only")]
    [InlineData(10.01, "usd", "US Dollars Ten and One Cent Only")]
    [InlineData(3.50, "GBP", "Pound Sterling Three and Fifty Pence Only")]
    [InlineData(1234567, "EUR", "Euro One Million Two Hundred Thirty Four Thousand Five Hundred Sixty Seven Only")]
    [InlineData(2500000000, "USD", "US Dollars Two Billion Five Hundred Million Only")]
    public void Other_currencies_are_written_in_full_words_with_international_numbering(decimal amount, string currency, string expected)
        => AmountInWords.Format(amount, currency).Should().Be(expected);

    [Fact]
    public void An_unknown_currency_keeps_its_code_and_a_hundredths_fraction()
        => AmountInWords.Format(10.25m, "XYZ").Should().Be("XYZ Ten and 25/100 Only");

    [Fact]
    public void Negative_amounts_carry_a_minus_prefix_after_the_currency_label()
        => AmountInWords.Format(-10m, "USD").Should().Be("US Dollars Minus Ten Only");
}
