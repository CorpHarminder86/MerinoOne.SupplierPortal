using System.Globalization;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// R18 (2026-11-03) — QuestPDF renderer for an ASN's Packing List (A4 portrait): the supplier's centred letterhead
/// (name, address, contact, GST / CIN), a light-blue "PACKING LIST" title band, the shipment facts in two columns, the
/// Ship From / Ship To boxes, the lines table (header repeated on every page), the Packing Summary beside the Package
/// Details, numbered remarks, the signature lines and a "Generated on / Page x of y" footer. No logo and no QR code.
///
/// <para>Renders EXACTLY what <see cref="PackingListDto"/> carries — it looks nothing up. Every null or blank value
/// prints as "-". Dates go through <see cref="AppTime"/> (local display) and quantities through
/// <see cref="PackingListFormat.Qty"/>, the same formatter the read model uses for its "LOTNO (qty)" entries.
/// <c>QuestPDF.Settings.License = LicenseType.Community</c> is set once in
/// <see cref="DependencyInjection.AddInfrastructure"/>.</para>
/// </summary>
public sealed class PackingListQuestPdfGenerator : IPackingListPdfGenerator
{
    private const string Ink = "#1f2937";
    private const string Navy = "#142C66";
    private const string Band = "#DCE9F7";
    private const string Rule = "#9DB8DD";
    private const string Muted = "#6b7280";

    /// <summary>What every null or blank value prints as.</summary>
    private const string Blank = "-";

    private const float Hairline = 0.5f;

    private static readonly string[] LineHeaders =
    {
        "Sl. No.", "PO Number", "PO Line", "Position", "Seq", "Item Code", "Item Description",
        "Shipped Qty", "UOM", "Serial Number / Lot Number", "Expiry Date",
    };

    private static readonly string[] PackageHeaders = { "Package No.", "Contents", "Quantity", "Gross Weight (KG)" };

    private enum Align { Left, Center, Right }

    public byte[] Generate(PackingListDto packingList) => Compose(packingList).GeneratePdf();

    private static IDocument Compose(PackingListDto packingList)
        => Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(t => t.FontSize(8.5f).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Element(c => SupplierHeader(c, packingList.Supplier));
                    col.Item().Element(TitleBand);
                    col.Item().Element(c => ShipmentFacts(c, packingList));
                    col.Item().Element(c => PartyBoxes(c, packingList));
                    col.Item().Element(c => LinesTable(c, packingList.Lines));
                    col.Item().ShowEntire().Element(c => PackingSummary(c, packingList));
                    col.Item().ShowEntire().Element(c => Remarks(c, packingList.Remarks));
                    col.Item().ShowEntire().Element(Signatures);
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"Generated on: {AppTime.Stamp(packingList.GeneratedAt)}")
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

    private static void SupplierHeader(IContainer c, PackingListPartyDto supplier)
        => c.Column(col =>
        {
            col.Item().Text(Show(supplier.Name)).FontSize(14).Bold().FontColor(Navy).AlignCenter();
            foreach (var line in AddressOrBlank(supplier.AddressLines))
                col.Item().Text(line).AlignCenter();
            col.Item().Text($"Call: {Show(supplier.Phone)} | Email: {Show(supplier.Email)}").AlignCenter();
            col.Item().Text($"GST No.: {Show(supplier.GstNo)} | CIN No.: {Show(supplier.Cin)}").AlignCenter();
        });

    private static void TitleBand(IContainer c)
        => c.Background(Band).Border(Hairline).BorderColor(Rule).PaddingVertical(4)
            .Text("PACKING LIST").FontSize(12).Bold().FontColor(Navy).LetterSpacing(0.1f).AlignCenter();

    // ── Shipment facts: two independent label/value columns, so a wrapped IRN only lengthens its own side ──

    private static void ShipmentFacts(IContainer c, PackingListDto pl)
        => c.Row(row =>
        {
            row.RelativeItem().Element(left => FactsTable(left, new[]
            {
                ("Packing List Number", Show(pl.PackingListNumber)),
                ("Packing List Date", pl.PackingListDate is { } date ? AppTime.Date(date) : Blank),
                ("ASN Number", Show(pl.AsnNumber)),
                ("Purchase Order", Show(pl.PurchaseOrders)),
                ("Shipment Number", Show(pl.ShipmentNumber)),
                ("E-way Bill No", Show(pl.EwayBillNo)),
                ("E-way Bill Date", Show(pl.EwayBillDate)),
            }));
            row.ConstantItem(8);
            row.RelativeItem().Element(right => FactsTable(right, new[]
            {
                ("Supplier Code", Show(pl.SupplierCode)),
                ("Currency", Show(pl.Currency)),
                ("Delivery Terms", Show(pl.DeliveryTerms)),
                ("Carrier", Show(pl.Carrier)),
                ("Vehicle No", Show(pl.VehicleNo)),
                ("Tracking No", Show(pl.TrackingNo)),
                ("IRN No", Show(pl.IrnNo)),
                ("IRN Date", Show(pl.IrnDate)),
            }));
        });

    /// <summary>
    /// Label / value rows in a bordered two-column grid. A value with no space to break at (a 64-character IRN, a long
    /// tracking number) is folded by the layout engine inside its cell — it never runs past it.
    /// </summary>
    private static void FactsTable(IContainer c, IEnumerable<(string Label, string Value)> rows, float labelWidth = 92)
        => c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(labelWidth);
                cd.RelativeColumn();
            });

            foreach (var (label, value) in rows)
            {
                table.Cell().Background(Band).Border(Hairline).BorderColor(Rule).Padding(3)
                    .Text(label).FontSize(8).SemiBold().FontColor(Navy);
                table.Cell().Border(Hairline).BorderColor(Rule).Padding(3)
                    .Text(value).FontSize(8);
            }
        });

    // ── Ship From / Ship To ──────────────────────────────────────────────────────────────────────────

    private static void PartyBoxes(IContainer c, PackingListDto pl)
        => c.Row(row =>
        {
            row.RelativeItem().Element(box => PartyBox(box, "Ship From (Supplier Warehouse)", pl.ShipFrom));
            row.ConstantItem(8);
            row.RelativeItem().Element(box => PartyBox(box, "Ship To (Customer Warehouse)", pl.ShipTo));
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

    // ── Lines ────────────────────────────────────────────────────────────────────────────────────────

    private static void LinesTable(IContainer c, IReadOnlyList<PackingListLineDto> lines)
        => c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(0.85f);   // Sl. No.
                cd.RelativeColumn(1.45f);   // PO Number
                cd.RelativeColumn(0.85f);   // PO Line
                cd.RelativeColumn(0.85f);   // Position
                cd.RelativeColumn(0.55f);   // Seq
                cd.RelativeColumn(1.4f);    // Item Code
                cd.RelativeColumn(2.1f);    // Item Description
                cd.RelativeColumn(1.1f);    // Shipped Qty
                cd.RelativeColumn(0.7f);    // UOM
                cd.RelativeColumn(2.1f);    // Serial Number / Lot Number
                cd.RelativeColumn(1.1f);    // Expiry Date
            });

            table.Header(h =>
            {
                foreach (var title in LineHeaders)
                    HeaderCell(h.Cell(), title);
            });

            // A table needs a body row: an ASN with no lines still prints one of dashes rather than a bare header.
            if (lines.Count == 0)
                for (var i = 0; i < LineHeaders.Length; i++) BodyCell(table.Cell(), Blank, Align.Center);

            foreach (var line in lines)
            {
                BodyCell(table.Cell(), line.SlNo.ToString(CultureInfo.InvariantCulture), Align.Center);
                BodyCell(table.Cell(), Show(line.PoNumber));
                BodyCell(table.Cell(), Show(line.PoLine), Align.Center);
                BodyCell(table.Cell(), Show(line.Position));
                BodyCell(table.Cell(), Show(line.Seq), Align.Center);
                BodyCell(table.Cell(), Show(line.ItemCode));
                BodyCell(table.Cell(), Show(line.ItemDescription));
                BodyCell(table.Cell(), PackingListFormat.Qty(line.ShippedQty, line.QtyDecimals), Align.Right);
                BodyCell(table.Cell(), Show(line.Uom), Align.Center);
                SerialLotCell(table.Cell(), line.SerialLotNumbers);
                BodyCell(table.Cell(), Show(line.ExpiryDate));
            }
        });

    private static void HeaderCell(IContainer cell, string title)
        => cell.Background(Band).Border(Hairline).BorderColor(Rule).PaddingVertical(3).PaddingHorizontal(2.5f)
            .AlignMiddle().Text(title).FontSize(7.5f).SemiBold().FontColor(Navy).AlignCenter();

    private static void BodyCell(IContainer cell, string value, Align align = Align.Left)
    {
        var text = Frame(cell).Text(value).FontSize(7.5f);
        switch (align)
        {
            case Align.Center: text.AlignCenter(); break;
            case Align.Right: text.AlignRight(); break;
        }
    }

    private static void SerialLotCell(IContainer cell, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0)
        {
            BodyCell(cell, Blank);
            return;
        }

        Frame(cell).Column(col =>
        {
            foreach (var entry in entries)
                col.Item().Text(entry).FontSize(7.5f);
        });
    }

    private static IContainer Frame(IContainer cell)
        => cell.Border(Hairline).BorderColor(Rule).PaddingVertical(2.5f).PaddingHorizontal(2.5f);

    // ── Summary, package details, remarks, signatures ────────────────────────────────────────────────

    /// <summary>
    /// Packing Summary beside Package Details. Only the shipped quantity is known to the portal; the package count, the
    /// weights, the dimensions and the package rows have no source and print "-" (one "-" row for the packages).
    /// </summary>
    private static void PackingSummary(IContainer c, PackingListDto pl)
        => c.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Element(SectionBand("Packing Summary"));
                col.Item().Element(body => FactsTable(body, new[]
                {
                    ("Total Shipped Quantity", PackingListFormat.Qty(pl.TotalShippedQty, pl.TotalQtyDecimals)),
                    ("Total Number of Packages", Blank),
                    ("Gross Weight", Blank),
                    ("Net Weight", Blank),
                    ("Dimensions", Blank),
                }, labelWidth: 120));
            });
            row.ConstantItem(8);
            row.RelativeItem().Column(col =>
            {
                col.Item().Element(SectionBand("Package Details"));
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(1f);
                        cd.RelativeColumn(1.3f);
                        cd.RelativeColumn(1f);
                        cd.RelativeColumn(1.3f);
                    });
                    foreach (var title in PackageHeaders)
                        HeaderCell(table.Cell(), title);
                    for (var i = 0; i < PackageHeaders.Length; i++) BodyCell(table.Cell(), Blank, Align.Center);
                });
            });
        });

    private static Action<IContainer> SectionBand(string title)
        => c => c.Background(Band).Border(Hairline).BorderColor(Rule).Padding(3)
            .Text(title).FontSize(8.5f).SemiBold().FontColor(Navy);

    /// <summary>The remarks arrive one per line; they print as a numbered list.</summary>
    private static void Remarks(IContainer c, string? remarks)
        => c.Column(col =>
        {
            col.Item().Text("Remarks:").SemiBold().FontColor(Navy);
            var items = (remarks ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0)
                col.Item().Text(Blank);
            for (var i = 0; i < items.Length; i++)
                col.Item().Text($"{i + 1}. {items[i]}");
        });

    private static void Signatures(IContainer c)
        => c.Row(row =>
        {
            row.RelativeItem().PaddingRight(40).Element(SignatureLine("Prepared By"));
            row.RelativeItem().PaddingLeft(40).Element(SignatureLine("Authorized By"));
        });

    private static Action<IContainer> SignatureLine(string label)
        => c => c.PaddingTop(34).BorderTop(Hairline).BorderColor(Navy).PaddingTop(2)
            .Text(label).FontSize(8).SemiBold().FontColor(Navy).AlignCenter();

    // ── Value helpers ────────────────────────────────────────────────────────────────────────────────

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? Blank : value.Trim();

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Blank;

    private static string Show(DateOnly? value)
        => value?.ToString(AppTime.DateFormat, CultureInfo.InvariantCulture) ?? Blank;

    private static IReadOnlyList<string> AddressOrBlank(IReadOnlyList<string> lines)
        => lines.Count == 0 ? new[] { Blank } : lines;
}
