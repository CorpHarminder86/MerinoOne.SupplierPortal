using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Application.Shipments;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

/// <summary>
/// 2026-11-17 feedback — the Supplier Invoice report BEFORE the ASN is posted. The R6 draft invoice is only created at
/// Post (it bills the PO line's shipped balance, which this ASN only adds to at Post), yet Post itself requires the
/// supplier's invoice number and invoice document. So, exactly like the Packing List, the ASN can print its invoice
/// straight from its own lines as soon as the buyer has Approved it (or from Draft for a supplier that needs no buyer
/// confirmation — <see cref="PackingListRules.IsAvailable"/>, the one shared availability rule).
///
/// <para>Nothing is persisted. One report per (currency, payment term) group, grouped and ordered like
/// <see cref="DraftInvoiceFromAsnFactory"/>. Quantity = the ASN's own shipped qty per PO line; price / discount / tax come
/// live from the PO line and the tax master through <see cref="InvoiceLineMath"/> (the same math the draft uses). The
/// draft Post creates bills the PO line's UNINVOICED shipped balance instead, so the two agree whenever earlier shipments
/// on the line are already invoiced (submitted) — always the case for the first ASN on a line. Invoice number = the ASN's
/// supplier-entered Invoice No. (suffixed "-n" when the ASN splits into several groups), else the draft placeholder
/// <c>DRAFT-{asn}-{n}</c>; invoice date = today (local).</para>
///
/// <para>Visibility is the ASN detail's (404 outside the caller's scope, decided before availability); an unavailable
/// status is a 409; a tax code without a usable rate is a 400 (the same tax gate that would block the draft).</para>
/// </summary>
public record GetAsnInvoiceReportsQuery(Guid AsnId) : IRequest<IReadOnlyList<SupplierInvoiceReportDto>>;

public class GetAsnInvoiceReportsQueryHandler : IRequestHandler<GetAsnInvoiceReportsQuery, IReadOnlyList<SupplierInvoiceReportDto>>
{
    private readonly IAppDbContext _db;
    private readonly IUnitDecimalsResolver _units;
    private readonly TaxRateResolver _taxResolver;

    public GetAsnInvoiceReportsQueryHandler(IAppDbContext db, IUnitDecimalsResolver units, TaxRateResolver taxResolver)
    {
        _db = db;
        _units = units;
        _taxResolver = taxResolver;
    }

    public async Task<IReadOnlyList<SupplierInvoiceReportDto>> Handle(GetAsnInvoiceReportsQuery request, CancellationToken ct)
    {
        // RLS-scoped visibility first (404), then availability (409) — same order as the Packing List.
        var dto = await AsnDtoBuilder.BuildAsync(_db, request.AsnId, ct);
        if (!PackingListRules.IsAvailable(dto.AsnStatus, dto.AsnConfirmationRequired))
            throw new ConflictException(dto.AsnConfirmationRequired
                ? $"The Invoice report is not available: ASN {dto.AsnNumber} is '{dto.AsnStatus}'. For a supplier that needs buyer confirmation it can be printed once the ASN is Approved, and after it is posted."
                : $"The Invoice report is not available: ASN {dto.AsnNumber} is '{dto.AsnStatus}'. For a supplier that needs no buyer confirmation it can be printed from Draft, and after the ASN is posted.");

        var asn = await _db.Asns.AsNoTracking().FirstAsync(a => a.Id == dto.Id, ct);

        var source = await (from al in _db.AsnLines
                            join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                            join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                            where al.AsnId == asn.Id && !al.IsDeleted
                            select new
                            {
                                al.ShippedQty,
                                PoLineId = pol.Id,
                                PoId = po.Id,
                                po.PoNumber,
                                po.CurrencyId,
                                po.CurrencyCode,
                                po.PaymentTermId,
                                PoPaymentTerms = po.PaymentTerms,
                                PoDeliveryTerms = po.DeliveryTerms,
                                pol.PositionNo,
                                pol.SequenceNo,
                                pol.OrderUnit,
                                pol.ItemCode,
                                pol.ItemDescription,
                                pol.PriceUnit,
                                pol.DiscountPct,
                                pol.DiscountAmount,
                                pol.TaxCode,
                                pol.TaxId,
                            }).ToListAsync(ct);

        // Two ASN lines on one PO line bill once, with their quantities summed.
        var lines = source
            .GroupBy(l => l.PoLineId)
            .Select(g => new { Line = g.First(), Qty = g.Sum(x => x.ShippedQty) })
            .Where(x => x.Qty > 0)
            .ToList();

        if (lines.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "The ASN has no shipped quantity to invoice." }
            });

        // ── Tax gate — identical to DraftInvoiceFromAsnFactory: every line needs a tax code with a usable rate ──
        var resolved = await _taxResolver.ResolveAsync(
            lines.Where(x => x.Line.TaxId.HasValue).Select(x => x.Line.TaxId!.Value), asn.TenantId, ct);
        var offenders = lines
            .Where(x => x.Line.TaxId is null || !resolved.TryGetValue(x.Line.TaxId.Value, out var t) || t.Rate is null)
            .Select(x => x.Line.TaxId.HasValue && resolved.TryGetValue(x.Line.TaxId.Value, out var t)
                ? $"'{t.Code}' (no rate)"
                : !string.IsNullOrWhiteSpace(x.Line.TaxCode)
                    ? $"'{x.Line.TaxCode!.Trim()}' (not resolvable)"
                    : $"item '{x.Line.ItemCode}' (no tax code)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (offenders.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceReport"] = new[]
                {
                    $"The Invoice report cannot be produced — tax code(s) without a usable rate: {string.Join(", ", offenders)}. Fix the tax master rate(s) first."
                }
            });

        // ── Shared header pieces ────────────────────────────────────────────────────────────────────────────────
        var supplierName = PackingListFormat.Blank(dto.SupplierName);
        var parties = await PackingListParties.LoadSupplierAsync(_db, asn.SupplierId, supplierName, ct);

        var companyId = asn.TenantEntityId;
        var companyName = companyId is { } cid
            ? await _db.TenantEntities.AsNoTracking().Where(e => e.Id == cid).Select(e => e.Name).FirstOrDefaultAsync(ct)
            : null;
        IReadOnlyList<string> baseLines = companyId is { } bid
            ? (await PackingListParties.CompanyAddressAsync(_db, a => a.TenantEntityId == bid && a.IsBaseAddress, ct)).Lines
            : [];
        var (warehouseName, shipToLines) = await PackingListParties.ResolveWarehouseAsync(
            _db, asn.WarehouseAddressSnapshot, asn.WarehouseAddressId, companyId, ct);

        var termIds = lines.Where(x => string.IsNullOrWhiteSpace(x.Line.PoPaymentTerms) && x.Line.PaymentTermId is not null)
            .Select(x => x.Line.PaymentTermId!.Value).Distinct().ToList();
        var termDescriptions = termIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.PaymentTerms.IgnoreQueryFilters().AsNoTracking()
                .Where(t => termIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Description, ct);

        var unitPlaces = await _units.ResolveAsync(lines.Select(x => x.Line.OrderUnit), ct);
        var driver = PackingListFormat.Pair(asn.DriverName, asn.DriverPhone);
        var today = AppTime.Now.Date;
        var generatedAt = AppTime.UtcNow;

        // ── Groups — same key and same order as DraftInvoiceFromAsnFactory ─────────────────────────────────
        var groups = lines
            .GroupBy(x => (x.Line.CurrencyId, Code: NormalizeCurrency(x.Line.CurrencyCode), x.Line.PaymentTermId))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .ThenBy(g => g.Key.PaymentTermId)
            .ThenBy(g => g.Key.CurrencyId)
            .ToList();

        var supplierInvoiceNo = PackingListFormat.Blank(asn.InvoiceNo);
        var reports = new List<SupplierInvoiceReportDto>(groups.Count);
        var groupSeq = 0;
        foreach (var group in groups)
        {
            groupSeq++;
            var ordered = group.OrderBy(x => x.Line.PoNumber).ThenBy(x => x.Line.PositionNo).ThenBy(x => x.Line.SequenceNo).ToList();

            var reportLines = ordered.Select((x, i) =>
            {
                var l = x.Line;
                var tax = resolved[l.TaxId!.Value];
                var discount = InvoiceLineMath.LineDiscount(
                    InvoiceLineMath.PerUnitDiscount(l.DiscountAmount, l.DiscountPct, l.PriceUnit), x.Qty);
                var taxable = InvoiceLineMath.Taxable(x.Qty, l.PriceUnit, discount);
                var lineTax = InvoiceLineMath.Tax(taxable, tax.Rate);
                return (Taxable: taxable, Dto: new SupplierInvoiceLineDto(
                    SlNo: i + 1,
                    PoNumber: l.PoNumber,
                    PoLine: l.PositionNo,
                    Position: null,
                    Seq: l.SequenceNo,
                    ItemCode: l.ItemCode,
                    ItemDescription: l.ItemDescription,
                    Qty: x.Qty,
                    QtyDecimals: !string.IsNullOrWhiteSpace(l.OrderUnit) && unitPlaces.TryGetValue(l.OrderUnit, out var places)
                        ? places : _units.Fallback,
                    Uom: l.OrderUnit,
                    UnitPrice: l.PriceUnit,
                    DiscountPct: l.DiscountPct != 0m ? l.DiscountPct : null,
                    DiscountAmount: discount,
                    TaxRatePct: tax.Rate,
                    TaxAmount: lineTax,
                    LineTotal: taxable + lineTax));
            }).ToList();

            var poRows = ordered.GroupBy(x => x.Line.PoId).Select(g => g.First().Line).OrderBy(p => p.PoNumber).ToList();
            var paymentTerms = poRows
                .Select(p => PackingListFormat.Blank(p.PoPaymentTerms)
                             ?? (p.PaymentTermId is { } id ? PackingListFormat.Blank(termDescriptions.GetValueOrDefault(id)) : null))
                .OfType<string>().Distinct().ToList();
            var deliveryTerms = poRows.Select(p => PackingListFormat.Blank(p.PoDeliveryTerms)).FirstOrDefault(t => t is not null)
                                ?? PackingListFormat.Blank(parties.DeliveryTermCode);

            var invoiceNumber = supplierInvoiceNo is null
                ? $"DRAFT-{asn.AsnNumber}-{groupSeq}"
                : groups.Count == 1 ? supplierInvoiceNo : $"{supplierInvoiceNo}-{groupSeq}";

            var totalDiscount = reportLines.Sum(r => r.Dto.DiscountAmount);
            var totalTaxable = reportLines.Sum(r => r.Taxable);
            var totalTax = reportLines.Sum(r => r.Dto.TaxAmount);
            var grandTotal = totalTaxable + totalTax;
            var currency = NormalizeCurrency(group.Key.Code);

            reports.Add(new SupplierInvoiceReportDto(
                Supplier: parties.Supplier,
                BillTo: new PackingListPartyDto(PackingListFormat.Blank(companyName), baseLines, null, null, null, null),
                ShipTo: new PackingListPartyDto(PackingListFormat.Pair(companyName, warehouseName), shipToLines, null, null, null, null),
                ShipFrom: parties.ShipFrom,
                InvoiceNumber: invoiceNumber,
                InvoiceDate: today,
                PurchaseOrders: PackingListFormat.Blank(string.Join(", ", poRows.Select(p => p.PoNumber))),
                AsnNumber: asn.AsnNumber,
                ShipmentNumber: asn.ErpCode,
                GrnNumber: null,
                Currency: currency,
                PaymentTerms: PackingListFormat.Blank(string.Join(", ", paymentTerms)),
                DeliveryTerms: deliveryTerms,
                SupplierCode: parties.SupplierCode,
                SupplierGstNo: parties.GstNumber,
                CustomerGstNo: null,
                AsnDate: asn.CreatedOn,
                ExpectedDelivery: asn.ExpectedDeliveryDate,
                Carrier: PackingListFormat.Carrier(asn.CarrierCode, asn.CarrierName),
                TrackingNo: asn.TrackingNumber,
                BillOfLadingNo: asn.BillOfLading,
                VehicleNo: asn.VehicleNumber,
                Driver: driver,
                PackingListNo: asn.PackingList,
                EwayBillNo: asn.EwayBillNo,
                EwayBillDate: asn.EwayBillDate,
                IrnNo: asn.IrnNo,
                IrnDate: asn.IrnDate,
                Lines: reportLines.Select(r => r.Dto).ToList(),
                TotalDiscount: totalDiscount,
                TotalTaxable: totalTaxable,
                TotalTax: totalTax,
                GrandTotal: grandTotal,
                AmountInWords: AmountInWords.Format(grandTotal, currency),
                Notes: null,
                GeneratedAt: generatedAt));
        }

        return reports;
    }

    private static string NormalizeCurrency(string? code)
        => string.IsNullOrWhiteSpace(code) ? "INR" : code.Trim().ToUpperInvariant();
}
