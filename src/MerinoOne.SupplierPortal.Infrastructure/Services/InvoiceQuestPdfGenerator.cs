using System.Reflection;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// R6 (2026-07-02, plan D13) — QuestPDF renderer for the invoice snapshot: branded letterhead (logo + number +
/// status), header facts (supplier, dates, IRN/ack/e-way), a ship-to block (ASN's live ship-to else the header
/// PO's point-in-time snapshot), the lines table, totals, and a signatory block. Renders EXACTLY what the detail
/// DTO carries — the frozen snapshot, never a live recomputation. Deliberately no amount-in-words line.
/// <c>QuestPDF.Settings.License = LicenseType.Community</c> is set once in
/// <see cref="DependencyInjection.AddInfrastructure"/>.
/// </summary>
public sealed class InvoiceQuestPdfGenerator : IInvoicePdfGenerator
{
    private const string Ink = "#1f2937";
    private const string Accent = "#0f3b5e";
    private const string Muted = "#6b7280";
    private const string RuleColor = "#d1d5db";

    // Loaded once per process from the embedded resource (Assets/merino-logo.png) — see the csproj comment.
    private static readonly byte[] LogoBytes = LoadLogo();

    private static byte[] LoadLogo()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("merino-logo.png", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        return mem.ToArray();
    }

    public byte[] Generate(InvoiceDetailDto invoice)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(9).FontColor(Ink));

                page.Header().Column(header =>
                {
                    header.Item().Row(row =>
                    {
                        row.ConstantItem(46).Height(46).Image(LogoBytes).FitArea();
                        row.ConstantItem(10);
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("MerinoOne Supplier Portal").FontSize(9).FontColor(Muted);
                            c.Item().Text("Invoice").FontSize(18).SemiBold().FontColor(Accent);
                            c.Item().Text(invoice.InvoiceNumber).FontSize(12).SemiBold();
                        });
                        row.ConstantItem(200).Column(c =>
                        {
                            c.Item().AlignRight().Text($"Status: {invoice.InvoiceStatus}").SemiBold();
                            c.Item().AlignRight().Text($"Origin: {invoice.InvoiceOrigin}").FontColor(Muted);
                            c.Item().AlignRight().Text($"Currency: {invoice.CurrencyCode}").FontColor(Muted);
                        });
                    });
                    header.Item().PaddingTop(8).LineHorizontal(0.8f).LineColor(Accent);
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    // ── Header facts ─────────────────────────────────────────────────────────────
                    col.Item().Row(row =>
                    {
                        row.RelativeItem(1.3f).Column(c =>
                        {
                            c.Item().Text("Supplier").FontColor(Muted).FontSize(8);
                            c.Item().Text($"{invoice.SupplierName} ({invoice.SupplierCode})").SemiBold();
                            if (!string.IsNullOrWhiteSpace(invoice.AsnNumber))
                                c.Item().PaddingTop(2).Text($"ASN: {invoice.AsnNumber}").FontColor(Muted);
                            if (invoice.PurchaseOrders.Count > 0)
                                c.Item().PaddingTop(2)
                                    .Text($"PO(s): {string.Join(", ", invoice.PurchaseOrders.Select(p => p.PoNumber))}")
                                    .FontColor(Muted);
                        });
                        row.RelativeItem().Column(c =>
                        {
                            Fact(c, "Invoice date", invoice.InvoiceDate.ToString("yyyy-MM-dd"));
                            Fact(c, "Submitted", invoice.SubmittedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "—");
                            Fact(c, "Matching", invoice.MatchingType);
                        });
                        row.RelativeItem().Column(c =>
                        {
                            Fact(c, "e-Invoice IRN", Blank(invoice.EInvoiceIrn));
                            Fact(c, "e-Invoice Ack No", Blank(invoice.EInvoiceAckNo));
                            Fact(c, "e-Way bill", Blank(invoice.EWayBillNumber));
                        });
                        row.RelativeItem(1.2f).Column(c =>
                        {
                            c.Item().Text("Ship to").FontColor(Muted).FontSize(8);
                            if (string.IsNullOrWhiteSpace(invoice.ShipToAddressName) && string.IsNullOrWhiteSpace(invoice.ShipToLine1))
                            {
                                c.Item().PaddingTop(1).Text("—");
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(invoice.ShipToAddressName))
                                    c.Item().PaddingTop(1).Text(invoice.ShipToAddressName!).SemiBold();
                                if (!string.IsNullOrWhiteSpace(invoice.ShipToLine1))
                                    c.Item().Text(invoice.ShipToLine1!);
                                if (!string.IsNullOrWhiteSpace(invoice.ShipToLine2))
                                    c.Item().Text(invoice.ShipToLine2!);
                                var cityLine = string.Join(", ", new[] { invoice.ShipToCity, invoice.ShipToState, invoice.ShipToPincode }
                                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                                if (!string.IsNullOrWhiteSpace(cityLine))
                                    c.Item().Text(cityLine);
                                if (!string.IsNullOrWhiteSpace(invoice.ShipToCountry))
                                    c.Item().Text(invoice.ShipToCountry!);
                            }
                        });
                    });

                    // ── Lines table ──────────────────────────────────────────────────────────────
                    col.Item().PaddingTop(14).Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.RelativeColumn(2.0f);   // Item
                            cd.RelativeColumn(3.0f);   // Description
                            cd.RelativeColumn(1.6f);   // PO
                            cd.RelativeColumn(1.1f);   // Qty
                            cd.RelativeColumn(1.2f);   // Unit price
                            cd.RelativeColumn(1.2f);   // Tax code
                            cd.RelativeColumn(1.0f);   // Tax %
                            cd.RelativeColumn(1.2f);   // Tax amt
                            cd.RelativeColumn(1.3f);   // Line amt
                        });

                        table.Header(h =>
                        {
                            HeaderCell(h.Cell(), "Item");
                            HeaderCell(h.Cell(), "Description");
                            HeaderCell(h.Cell(), "PO");
                            HeaderCell(h.Cell(), "Billed qty", right: true);
                            HeaderCell(h.Cell(), "Unit price", right: true);
                            HeaderCell(h.Cell(), "Tax code");
                            HeaderCell(h.Cell(), "Tax %", right: true);
                            HeaderCell(h.Cell(), "Tax amt", right: true);
                            HeaderCell(h.Cell(), "Line amt", right: true);
                        });

                        foreach (var line in invoice.Lines)
                        {
                            BodyCell(table.Cell(), line.ItemCode);
                            BodyCell(table.Cell(), line.ItemDescription ?? "—");
                            BodyCell(table.Cell(), line.PoNumber ?? "—");
                            BodyCell(table.Cell(), line.BilledQty.ToString("0.####"), right: true);
                            BodyCell(table.Cell(), line.UnitPrice.ToString("N2"), right: true);
                            BodyCell(table.Cell(), line.TaxCode ?? "—");
                            BodyCell(table.Cell(), line.TaxRatePct is { } r ? r.ToString("0.##") : "—", right: true);
                            BodyCell(table.Cell(), line.TaxAmount.ToString("N2"), right: true);
                            BodyCell(table.Cell(), line.LineAmount.ToString("N2"), right: true);
                        }
                    });

                    // ── Totals block ─────────────────────────────────────────────────────────────
                    col.Item().PaddingTop(12).AlignRight().Column(t =>
                    {
                        TotalRow(t, "Invoice amount", invoice.InvoiceAmount, invoice.CurrencyCode);
                        TotalRow(t, "Tax amount", invoice.TaxAmount, invoice.CurrencyCode);
                        t.Item().PaddingTop(2).BorderTop(0.8f).BorderColor(Accent).PaddingTop(2).Row(r =>
                        {
                            r.ConstantItem(140).Text("Net amount").SemiBold();
                            r.ConstantItem(120).AlignRight()
                                .Text($"{invoice.NetAmount:N2} {invoice.CurrencyCode}").SemiBold();
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(invoice.Notes))
                    {
                        col.Item().PaddingTop(12).Column(c =>
                        {
                            c.Item().Text("Notes").FontColor(Muted).FontSize(8);
                            c.Item().Text(invoice.Notes!);
                        });
                    }

                    // ── Signatory block ──────────────────────────────────────────────────────────
                    col.Item().PaddingTop(28).Row(row =>
                    {
                        row.RelativeItem();
                        row.ConstantItem(200).Column(c =>
                        {
                            c.Item().Text($"For {invoice.SupplierName}").SemiBold();
                            c.Item().PaddingTop(30).BorderTop(0.8f).BorderColor(RuleColor);
                            c.Item().PaddingTop(3).AlignCenter().Text("Authorized Signatory").FontSize(8).FontColor(Muted);
                        });
                    });
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm 'UTC'} · MerinoOne Supplier Portal")
                        .FontSize(7).FontColor(Muted);
                    row.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(7).FontColor(Muted));
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static void Fact(ColumnDescriptor c, string label, string value)
    {
        c.Item().PaddingBottom(1).Text(label).FontColor(Muted).FontSize(8);
        c.Item().PaddingBottom(4).Text(value);
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value!;

    private static void HeaderCell(IContainer cell, string text, bool right = false)
    {
        var c = cell.BorderBottom(0.8f).BorderColor(Accent).PaddingVertical(3).PaddingHorizontal(2);
        (right ? c.AlignRight() : c).Text(text).SemiBold().FontSize(8).FontColor(Accent);
    }

    private static void BodyCell(IContainer cell, string text, bool right = false)
    {
        var c = cell.BorderBottom(0.4f).BorderColor(RuleColor).PaddingVertical(3).PaddingHorizontal(2);
        (right ? c.AlignRight() : c).Text(text);
    }

    private static void TotalRow(ColumnDescriptor col, string label, decimal amount, string currency)
        => col.Item().Row(r =>
        {
            r.ConstantItem(140).Text(label).FontColor(Muted);
            r.ConstantItem(120).AlignRight().Text($"{amount:N2} {currency}");
        });
}
