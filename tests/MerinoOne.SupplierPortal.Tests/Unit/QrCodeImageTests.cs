using System.Text;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using QuestPDF.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R19 — the QR helper behind the Packing List letterhead, and the letterhead rendering with and without a QR.
/// Pure, DB-less: the host that sets the QuestPDF license in <c>AddInfrastructure</c> is not running, so the test sets it
/// itself.
/// </summary>
public class QrCodeImageTests
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47 };

    static QrCodeImageTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public void Png_returns_a_png_for_an_ASN_number()
    {
        var png = QrCodeImage.Png("ASN-S0013-20261029055306091");

        png.Length.Should().BeGreaterThan(100);
        png.Take(4).Should().Equal(PngMagic);
    }

    [Fact]
    public void A_longer_value_needs_a_larger_symbol_so_the_image_is_not_smaller()
    {
        var shortPng = QrCodeImage.Png("ASN-1");
        var longPng = QrCodeImage.Png(new string('X', 120));

        longPng.Length.Should().BeGreaterThanOrEqualTo(shortPng.Length);
    }

    [Fact]
    public void A_larger_pixelsPerModule_gives_a_larger_image()
        => QrCodeImage.Png("ASN-1", pixelsPerModule: 20).Length
            .Should().BeGreaterThan(QrCodeImage.Png("ASN-1", pixelsPerModule: 4).Length);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_null_or_blank_value_throws_ArgumentException(string? value)
    {
        var act = () => QrCodeImage.Png(value!);

        act.Should().Throw<ArgumentException>();
    }

    private static readonly PackingListPartyDto Party = new(
        "Acme Textiles Private Limited",
        new[] { "Plot 14, MIDC Industrial Area", "Mumbai - 400093, Maharashtra, India" },
        "+91 98200 12345", "dispatch@acme.example", "27AAACA1234A1Z5", null);

    private static PackingListDto Dto(string asnNumber) => new(
        Party, Party, Party, "PL-1", new DateTime(2026, 11, 3, 4, 30, 0, DateTimeKind.Utc), asnNumber,
        "PO-1001", "LN-ASN-1", "SUP-1", "INR", "FOB Mumbai", "CAR-T1 - Test Carrier", "MH12AB1234", "TRK-1",
        "291000123456", new DateOnly(2026, 11, 2), new string('a', 64), new DateOnly(2026, 11, 1),
        new[]
        {
            new PackingListLineDto(1, "PO-1001", 10, null, 1, "ITM-0001", "Item 1 description", 5, 2, "EA",
                Array.Empty<string>(), null),
        },
        5m, 2, "This packing list is generated based on ASN " + asnNumber + ".",
        new DateTime(2026, 11, 3, 9, 15, 0, DateTimeKind.Utc));

    [Fact]
    public void The_packing_list_renders_a_pdf_with_the_qr_for_an_ASN_number()
    {
        var pdf = new PackingListQuestPdfGenerator().Generate(Dto("ASN-S0013-20261029055306091"));

        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public void The_packing_list_renders_a_pdf_without_a_qr_when_the_ASN_number_is_blank()
    {
        var pdf = new PackingListQuestPdfGenerator().Generate(Dto(" "));

        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }
}
