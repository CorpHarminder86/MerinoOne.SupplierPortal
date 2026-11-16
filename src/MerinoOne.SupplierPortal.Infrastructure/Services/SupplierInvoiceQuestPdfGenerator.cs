using System.Globalization;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// R19 (2026-11-16) — QuestPDF renderer for the Supplier Invoice (A4 portrait), in the same visual language as the Packing List:
/// the supplier's centred letterhead with the invoice number's QR code at its top right, a "SUPPLIER INVOICE" title band, the
/// invoice facts in two columns, the Bill To / Ship To / Ship From boxes, the shipment box (ASN, carrier, e-way bill, IRN — left
/// out when there is nothing to show), the lines table (header repeated on every page), the amount in words beside the summary,
/// numbered notes, the authorised-signatory block and a "Generated on / Page x of y" footer.
///
/// <para>Renders EXACTLY what <see cref="SupplierInvoiceReportDto"/> carries — it looks nothing up and recomputes nothing. Every
/// null or blank value prints as "-". Dates go through <see cref="AppTime"/> (local display), quantities through
/// <see cref="PackingListFormat.Qty"/>, money with two decimals and thousand separators (invariant culture, so the output is the
/// same on any host). <c>QuestPDF.Settings.License = LicenseType.Community</c> is set once in
/// <see cref="DependencyInjection.AddInfrastructure"/>.</para>
/// </summary>
public sealed class SupplierInvoiceQuestPdfGenerator : ISupplierInvoicePdfGenerator
{
    private const string Ink = "#1f2937";
    private const string Navy = "#142C66";
    private const string Band = "#DCE9F7";
    private const string Rule = "#9DB8DD";
    private const string Muted = "#6b7280";
    private const string Highlight = "#FFF4CC";

    /// <summary>What every null or blank value prints as.</summary>
    private const string Blank = "-";

    private const float Hairline = 0.5f;

    private enum Align { Left, Center, Right }

    public byte[] Generate(SupplierInvoiceReportDto dto) => Compose(dto).GeneratePdf();

    private static IDocument Compose(SupplierInvoiceReportDto dto)
        => Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(24);
                page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Element(c => Letterhead(c, dto.Supplier, dto.InvoiceNumber));
                    col.Item().Element(TitleBand);
                    col.Item().Element(c => InvoiceFacts(c, dto));
                    col.Item().Element(c => PartyBoxes(c, dto));
                    if (HasShipment(dto))
                        col.Item().Element(c => ShipmentBox(c, dto));
                    col.Item().Element(c => LinesTable(c, dto));
                    col.Item().ShowEntire().Element(c => WordsAndSummary(c, dto));
                    col.Item().ShowEntire().Element(c => Notes(c, dto));
                    col.Item().ShowEntire().Element(c => Signatory(c, dto.Supplier.Name));
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"Generated on: {AppTime.Stamp(dto.GeneratedAt)}")
                        .FontSize(7.5f).FontColor(Muted);
                    row.ConstantItem(90).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Muted));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                    });
                });
            });
        });

    // ── Letterhead + title ───────────────────────────────────────────────────────────────────────────

    /// <summary>Width of the QR column at the letterhead's right; an equal spacer on the left keeps the text centred on the page.</summary>
    private const float QrColumnWidth = 84;

    private const float QrSize = 64;

    /// <summary>
    /// The centred letterhead with the invoice's QR code (value = the invoice number) and its caption at the top right. The left
    /// spacer is as wide as the QR column so the text stays centred on the page; a blank invoice number renders no QR.
    /// </summary>
    private static void Letterhead(IContainer c, PackingListPartyDto supplier, string? invoiceNumber)
        => c.Row(row =>
        {
            row.ConstantItem(QrColumnWidth);

            row.RelativeItem().Column(col =>
            {
                col.Item().Text(Show(supplier.Name)).FontSize(14).Bold().FontColor(Navy).AlignCenter();
                foreach (var line in AddressOrBlank(supplier.AddressLines))
                    col.Item().Text(line).AlignCenter();
                col.Item().Text($"Call: {Show(supplier.Phone)} | Email: {Show(supplier.Email)}").AlignCenter();
                col.Item().Text($"GST No.: {Show(supplier.GstNo)} | CIN No.: {Show(supplier.Cin)}").AlignCenter();
            });

            row.ConstantItem(QrColumnWidth).Element(qr =>
            {
                if (string.IsNullOrWhiteSpace(invoiceNumber)) return;
                qr.Column(col =>
                {
                    col.Item().AlignCenter().Width(QrSize).Height(QrSize).Image(QrCodeImage.Png(invoiceNumber.Trim()));
                    col.Item().Text("Scan for Invoice Details").FontSize(6.5f).FontColor(Navy).AlignCenter();
                });
            });
        });

    private static void TitleBand(IContainer c)
        => c.Background(Band).Border(Hairline).BorderColor(Rule).PaddingVertical(4)
            .Text("SUPPLIER INVOICE").FontSize(12).Bold().FontColor(Navy).LetterSpacing(0.1f).AlignCenter();

    // ── Invoice facts: two independent label/value columns, so a wrapped value only lengthens its own side ──

    private static void InvoiceFacts(IContainer c, SupplierInvoiceReportDto dto)
        => c.Row(row =>
        {
            row.RelativeItem().Element(left => FactsTable(left, new[]
            {
                ("Invoice Number", Show(dto.InvoiceNumber), true),
                ("Invoice Date", AppTime.Date(dto.InvoiceDate), false),
                ("Purchase Order", Show(dto.PurchaseOrders), false),
                ("ASN Number", Show(dto.AsnNumber), false),
                ("Shipment Number", Show(dto.ShipmentNumber), false),
                ("GRN Number", Show(dto.GrnNumber), false),
            }));
            row.ConstantItem(8);
            row.RelativeItem().Element(right => FactsTable(right, new[]
            {
                ("Currency", Show(dto.Currency), false),
                ("Payment Terms", Show(dto.PaymentTerms), false),
                ("Delivery Terms", Show(dto.DeliveryTerms), false),
                ("Supplier Code", Show(dto.SupplierCode), false),
                ("Supplier GST No", Show(dto.SupplierGstNo), false),
                ("Customer GST No", Show(dto.CustomerGstNo), false),
            }));
        });

    /// <summary>
    /// Label / value rows in a bordered two-column grid. A value with no space to break at (a 64-character IRN, a long
    /// tracking number) is folded by the layout engine inside its cell — it never runs past it.
    /// </summary>
    private static void FactsTable(IContainer c, IEnumerable<(string Label, string Value, bool BoldValue)> rows, float labelWidth = 92)
        => c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(labelWidth);
                cd.RelativeColumn();
            });

            foreach (var (label, value, boldValue) in rows)
            {
                table.Cell().Background(Band).Border(Hairline).BorderColor(Rule).Padding(3)
                    .Text(label).FontSize(8).SemiBold().FontColor(Navy);
                var valueText = table.Cell().Border(Hairline).BorderColor(Rule).Padding(3).Text(value).FontSize(8);
                if (boldValue) valueText.Bold();
            }
        });

    // ── Bill To / Ship To / Ship From ────────────────────────────────────────────────────────────────

    private static void PartyBoxes(IContainer c, SupplierInvoiceReportDto dto)
        => c.Row(row =>
        {
            row.RelativeItem().Element(box => PartyBox(box, "Bill To", dto.BillTo));
            row.ConstantItem(6);
            row.RelativeItem().Element(box => PartyBox(box, "Ship To (Customer Warehouse)", dto.ShipTo));
            row.ConstantItem(6);
            row.RelativeItem().Element(box => PartyBox(box, "Ship From (Supplier Warehouse)", dto.ShipFrom));
        });

    private static void PartyBox(IContainer c, string title, PackingListPartyDto party)
        => c.Border(Hairline).BorderColor(Rule).Column(col =>
        {
            col.Item().Background(Band).BorderBottom(Hairline).BorderColor(Rule).Padding(3)
                .Text(title).FontSize(8.5f).SemiBold().FontColor(Navy);
            col.Item().Padding(5).Column(body =>
            {
                body.Item().Text(Show(party.Name)).SemiBold();
                foreach (var line in AddressOrBlank(party.AddressLines))
                    body.Item().Text(line);
            });
        });

    // ── Shipment box ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shipment box is printed when the invoice has an ASN or carries an e-way bill / IRN; otherwise it is left out.</summary>
    private static bool HasShipment(SupplierInvoiceReportDto dto)
        => !string.IsNullOrWhiteSpace(dto.AsnNumber)
           || !string.IsNullOrWhiteSpace(dto.EwayBillNo)
           || !string.IsNullOrWhiteSpace(dto.IrnNo);

    private static void ShipmentBox(IContainer c, SupplierInvoiceReportDto dto)
        => c.Column(col =>
        {
            col.Item().Element(SectionBand("Shipment Details"));
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(left => FactsTable(left, new[]
                {
                    ("ASN Date", dto.AsnDate is { } asnDate ? AppTime.Date(asnDate) : Blank, false),
                    ("Expected Delivery", dto.ExpectedDelivery is { } expected ? AppTime.Date(expected) : Blank, false),
                    ("Carrier", Show(dto.Carrier), false),
                    ("Tracking No", Show(dto.TrackingNo), false),
                }));
                row.ConstantItem(8);
                row.RelativeItem().Element(right => FactsTable(right, new[]
                {
                    ("Bill of Lading No", Show(dto.BillOfLadingNo), false),
                    ("Vehicle No", Show(dto.VehicleNo), false),
                    ("Driver", Show(dto.Driver), false),
                    ("Packing List No", Show(dto.PackingListNo), false),
                }));
            });
            col.Item().Element(c2 => StatutoryRows(c2, dto));
        });

    /// <summary>The e-way bill and IRN references on highlighted rows: label / value / label / value.</summary>
    private static void StatutoryRows(IContainer c, SupplierInvoiceReportDto dto)
        => c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(92);
                cd.RelativeColumn(1.6f);
                cd.ConstantColumn(92);
                cd.RelativeColumn(1f);
            });

            HighlightRow(table, "E-way Bill Number", Show(dto.EwayBillNo), "E-way Bill Date", Show(dto.EwayBillDate));
            HighlightRow(table, "IRN Number", Show(dto.IrnNo), "IRN Date", Show(dto.IrnDate));
        });

    private static void HighlightRow(TableDescriptor table, string label1, string value1, string label2, string value2)
    {
        table.Cell().Background(Highlight).Border(Hairline).BorderColor(Rule).Padding(3)
            .Text(label1).FontSize(8).SemiBold().FontColor(Navy);
        table.Cell().Background(Highlight).Border(Hairline).BorderColor(Rule).Padding(3).Text(value1).FontSize(8);
        table.Cell().Background(Highlight).Border(Hairline).BorderColor(Rule).Padding(3)
            .Text(label2).FontSize(8).SemiBold().FontColor(Navy);
        table.Cell().Background(Highlight).Border(Hairline).BorderColor(Rule).Padding(3).Text(value2).FontSize(8);
    }

    // ── Lines ────────────────────────────────────────────────────────────────────────────────────────

    private static string[] LineHeaders(string? currency)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim();
        string Titled(string title) => code is null ? title : $"{title} ({code})";
        return new[]
        {
            "Sl. No.", "PO Number", "PO Line", "Position", "Seq", "Item Code", "Item Description",
            "Shipped Qty", "UOM", Titled("Unit Price"), "Discount %", Titled("Discount Amount"),
            "Tax Rate (%)", Titled("Tax Amount"), Titled("Line Total"),
        };
    }

    private static void LinesTable(IContainer c, SupplierInvoiceReportDto dto)
        => c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(0.5f);    // Sl. No.
                cd.RelativeColumn(1.1f);    // PO Number
                cd.RelativeColumn(0.6f);    // PO Line
                cd.RelativeColumn(0.7f);    // Position
                cd.RelativeColumn(0.45f);   // Seq
                cd.RelativeColumn(1.1f);    // Item Code
                cd.RelativeColumn(1.5f);    // Item Description
                cd.RelativeColumn(0.9f);    // Shipped Qty
                cd.RelativeColumn(0.55f);   // UOM
                cd.RelativeColumn(1.15f);   // Unit Price
                cd.RelativeColumn(0.75f);   // Discount %
                cd.RelativeColumn(1.1f);    // Discount Amount
                cd.RelativeColumn(0.75f);   // Tax Rate (%)
                cd.RelativeColumn(1.1f);    // Tax Amount
                cd.RelativeColumn(1.2f);    // Line Total
            });

            var headers = LineHeaders(dto.Currency);
            table.Header(h =>
            {
                foreach (var title in headers)
                    HeaderCell(h.Cell(), title);
            });

            // A table needs a body row: an invoice with no lines still prints one of dashes rather than a bare header.
            if (dto.Lines.Count == 0)
                for (var i = 0; i < headers.Length; i++) BodyCell(table.Cell(), Blank, Align.Center);

            foreach (var line in dto.Lines)
            {
                BodyCell(table.Cell(), line.SlNo.ToString(CultureInfo.InvariantCulture), Align.Center);
                BodyCell(table.Cell(), Show(line.PoNumber));
                BodyCell(table.Cell(), Show(line.PoLine), Align.Center);
                BodyCell(table.Cell(), Show(line.Position));
                BodyCell(table.Cell(), Show(line.Seq), Align.Center);
                BodyCell(table.Cell(), Show(line.ItemCode));
                BodyCell(table.Cell(), Show(line.ItemDescription));
                BodyCell(table.Cell(), PackingListFormat.Qty(line.Qty, line.QtyDecimals), Align.Right);
                BodyCell(table.Cell(), Show(line.Uom), Align.Center);
                BodyCell(table.Cell(), Money(line.UnitPrice), Align.Right);
                BodyCell(table.Cell(), Show(line.DiscountPct), Align.Right);
                BodyCell(table.Cell(), Money(line.DiscountAmount), Align.Right);
                BodyCell(table.Cell(), Show(line.TaxRatePct), Align.Right);
                BodyCell(table.Cell(), Money(line.TaxAmount), Align.Right);
                BodyCell(table.Cell(), Money(line.LineTotal), Align.Right);
            }
        });

    private static void HeaderCell(IContainer cell, string title)
        => cell.Background(Band).Border(Hairline).BorderColor(Rule).PaddingVertical(3).PaddingHorizontal(2)
            .AlignMiddle().Text(title).FontSize(6.5f).SemiBold().FontColor(Navy).AlignCenter();

    private static void BodyCell(IContainer cell, string value, Align align = Align.Left)
    {
        var text = cell.Border(Hairline).BorderColor(Rule).PaddingVertical(2.5f).PaddingHorizontal(2)
            .Text(value).FontSize(7);
        switch (align)
        {
            case Align.Center: text.AlignCenter(); break;
            case Align.Right: text.AlignRight(); break;
        }
    }

    // ── Amount in words + summary, notes, signatory ──────────────────────────────────────────────────

    private static void WordsAndSummary(IContainer c, SupplierInvoiceReportDto dto)
        => c.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Element(SectionBand("Amount in Words:"));
                col.Item().Border(Hairline).BorderColor(Rule).Padding(5)
                    .Text(Show(dto.AmountInWords)).FontSize(8.5f).SemiBold();
            });
            row.ConstantItem(8);
            row.ConstantItem(240).Column(col =>
            {
                col.Item().Element(SectionBand("Summary"));
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1.6f);
                        cd.RelativeColumn(1f);
                    });

                    SummaryRow(table, $"Total Discount Amount{CurrencySuffix(dto.Currency)}", Money(dto.TotalDiscount), grand: false);
                    SummaryRow(table, $"Total Taxable Amount{CurrencySuffix(dto.Currency)}", Money(dto.TotalTaxable), grand: false);
                    SummaryRow(table, $"Total Tax Amount{CurrencySuffix(dto.Currency)}", Money(dto.TotalTax), grand: false);
                    SummaryRow(table, $"Grand Total{CurrencySuffix(dto.Currency)}", Money(dto.GrandTotal), grand: true);
                });
            });
        });

    private static void SummaryRow(TableDescriptor table, string label, string value, bool grand)
    {
        var labelCell = table.Cell().Border(Hairline).BorderColor(Rule).Padding(3);
        var valueCell = table.Cell().Border(Hairline).BorderColor(Rule).Padding(3);
        if (grand)
        {
            labelCell = labelCell.Background(Band);
            valueCell = valueCell.Background(Band);
        }

        var labelText = labelCell.Text(label).FontSize(8).FontColor(Navy);
        var valueText = valueCell.Text(value).FontSize(8).AlignRight();
        if (grand)
        {
            labelText.Bold();
            valueText.Bold();
        }
        else
        {
            labelText.SemiBold();
        }
    }

    private static string CurrencySuffix(string? currency)
        => string.IsNullOrWhiteSpace(currency) ? string.Empty : $" ({currency.Trim()})";

    private static Action<IContainer> SectionBand(string title)
        => c => c.Background(Band).Border(Hairline).BorderColor(Rule).Padding(3)
            .Text(title).FontSize(8.5f).SemiBold().FontColor(Navy);

    /// <summary>Notes print as a numbered list: the standing sentences first, then the invoice's own notes when it has any.</summary>
    private static void Notes(IContainer c, SupplierInvoiceReportDto dto)
        => c.Column(col =>
        {
            col.Item().Text("Notes:").SemiBold().FontColor(Navy);

            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(dto.AsnNumber))
                notes.Add($"This is a system generated invoice based on ASN {dto.AsnNumber.Trim()}.");
            notes.Add("Please make the payment as per the agreed payment terms.");
            if (!string.IsNullOrWhiteSpace(dto.Notes))
                notes.Add(string.Join(' ', dto.Notes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

            for (var i = 0; i < notes.Count; i++)
                col.Item().Text($"{i + 1}. {notes[i]}");
        });

    private static void Signatory(IContainer c, string? supplierName)
        => c.Row(row =>
        {
            row.RelativeItem();
            row.ConstantItem(200).PaddingTop(34).BorderTop(Hairline).BorderColor(Navy).PaddingTop(2).Column(col =>
            {
                col.Item().Text("Authorized Signatory").FontSize(8).SemiBold().FontColor(Navy).AlignCenter();
                col.Item().Text(Show(supplierName)).FontSize(8).AlignCenter();
            });
        });

    // ── Value helpers ────────────────────────────────────────────────────────────────────────────────

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? Blank : value.Trim();

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Blank;

    private static string Show(decimal? value) => value is { } v ? Money(v) : Blank;

    private static string Show(DateOnly? value)
        => value?.ToString(AppTime.DateFormat, CultureInfo.InvariantCulture) ?? Blank;

    /// <summary>Two decimals with thousand separators ("1,000.00"), the invariant culture so every host prints the same.</summary>
    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> AddressOrBlank(IReadOnlyList<string> lines)
        => lines.Count == 0 ? new[] { Blank } : lines;
}
