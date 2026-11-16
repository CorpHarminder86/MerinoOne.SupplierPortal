using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using QuestPDF.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R19 — the QuestPDF renderer turns every shape of <see cref="SupplierInvoiceReportDto"/> into a real PDF and never throws.
/// DB-less: the host that sets the QuestPDF license in <c>AddInfrastructure</c> is not running, so the test sets it itself.
/// </summary>
public class SupplierInvoicePdfGeneratorTests
{
    private static readonly SupplierInvoiceQuestPdfGenerator Generator = new();

    static SupplierInvoicePdfGeneratorTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly PackingListPartyDto Supplier = new(
        "Acme Textiles Private Limited",
        new[] { "Plot 14, MIDC Industrial Area", "Mumbai - 400093, Maharashtra, India" },
        "+91 98200 12345", "accounts@acme.example", "27AAACA1234A1Z5", "U17110MH2001PTC123456");

    private static readonly PackingListPartyDto BillTo = new(
        "Merino Industries Limited", new[] { "Registered Office, Sector 5", "Gurugram - 122001, Haryana, India" },
        null, null, null, null);

    private static readonly PackingListPartyDto ShipTo = new(
        "Merino Industries Limited – Hapur Warehouse", new[] { "Village Dasna, NH-9", "Hapur - 245101, Uttar Pradesh, India" },
        null, null, null, null);

    private static readonly PackingListPartyDto NoParty = new(null, Array.Empty<string>(), null, null, null, null);

    private static SupplierInvoiceLineDto Line(int sl, decimal unitPrice = 1250m, decimal discountAmount = 125m, decimal tax = 202.5m)
        => new(sl, "PO-1001", 10 * sl, null, 1, $"ITM-{sl:0000}", $"Laminated board item {sl}", 10m, 2, "EA",
            unitPrice, 10m, discountAmount, 18m, tax, unitPrice * 10m - discountAmount + tax);

    /// <summary>Three lines like the sample invoice the layout was drawn from.</summary>
    private static SupplierInvoiceReportDto Dto(IReadOnlyList<SupplierInvoiceLineDto> lines, string invoiceNumber = "INV-2026-00042")
    {
        var discount = lines.Sum(l => l.DiscountAmount);
        var taxable = lines.Sum(l => l.LineTotal - l.TaxAmount);
        var tax = lines.Sum(l => l.TaxAmount);
        return new SupplierInvoiceReportDto(
            Supplier, BillTo, ShipTo, Supplier,
            invoiceNumber, new DateTime(2026, 11, 14, 6, 30, 0, DateTimeKind.Utc),
            "PO-1001, PO-1002", "ASN-000123", "LN-ASN-1", "GRN-778", "INR",
            "Net 30 Days", "FOB Mumbai", "SUP-1", "27AAACA1234A1Z5", null,
            new DateTime(2026, 11, 10, 6, 30, 0, DateTimeKind.Utc), new DateTime(2026, 11, 16, 6, 30, 0, DateTimeKind.Utc),
            "CAR-T1 – Test Carrier", "TRK-1", "BL-55", "MH12AB1234", "R. Kumar – 9820012345", "PL-1",
            "291000123456", new DateOnly(2026, 11, 12), new string('a', 64), new DateOnly(2026, 11, 12),
            lines, discount, taxable, tax, taxable + tax, "Indian Rupees Sixteen Thousand Eight Hundred Forty Only",
            "Goods as per PO.", new DateTime(2026, 11, 16, 9, 15, 0, DateTimeKind.Utc));
    }

    /// <summary>The page tree is written in the clear, so the pages are the "/Type /Page" objects (not "/Pages").</summary>
    private static int PageCount(byte[] pdf)
        => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    private static byte[] RenderPdf(SupplierInvoiceReportDto dto)
    {
        var bytes = Generator.Generate(dto);
        bytes.Length.Should().BeGreaterThan(1000, because: "a rendered A4 document is never a stub");
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        return bytes;
    }

    [Fact]
    public void A_sample_like_invoice_with_three_lines_renders_one_page()
        => PageCount(RenderPdf(Dto(new[] { Line(1), Line(2, 800m, 0m, 144m), Line(3, 2500m, 250m, 405m) })))
            .Should().Be(1);

    [Fact]
    public void An_invoice_with_no_asn_and_every_optional_field_missing_still_renders_one_page()
    {
        var dto = new SupplierInvoiceReportDto(
            NoParty, NoParty, NoParty, NoParty,
            "INV-1", DateTime.UtcNow, null, null, null, null, "INR", null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, null, null,
            new[] { new SupplierInvoiceLineDto(1, null, null, null, null, null, null, 0m, 0, null, 0m, null, 0m, null, 0m, 0m) },
            0m, 0m, 0m, 0m, "Indian Rupees Zero Only", null, DateTime.UtcNow);

        PageCount(RenderPdf(dto)).Should().Be(1);
    }

    [Fact]
    public void An_invoice_with_no_lines_still_renders()
        => RenderPdf(Dto(Array.Empty<SupplierInvoiceLineDto>()));

    [Fact]
    public void More_lines_than_fit_one_page_flow_onto_further_pages()
        => PageCount(RenderPdf(Dto(Enumerable.Range(1, 130).Select(i => Line(i)).ToList())))
            .Should().BeGreaterThan(2);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_invoice_number_does_not_throw(string invoiceNumber)
        => RenderPdf(Dto(new[] { Line(1) }, invoiceNumber));
}
