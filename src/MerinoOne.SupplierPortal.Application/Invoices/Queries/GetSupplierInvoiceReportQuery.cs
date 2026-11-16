using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

/// <summary>
/// R19 (2026-11-16) — the read model behind the Supplier Invoice PDF: the supplier, bill-to, ship-to and ship-from blocks, the
/// invoice / ASN / shipment header, the line rows, the totals and the amount in words, resolved once so the renderer does no
/// lookups.
///
/// <para>Visibility is exactly <see cref="GetInvoiceByIdQuery"/>'s: an invoice that does not exist or that the caller's RLS /
/// seccode scope hides is a 404. The supplier block, Ship From and the receiving warehouse come from the same resolution the
/// Packing List uses (<see cref="PackingListParties"/>). The report only READS the stored line values — discount, taxable
/// amount and tax were frozen when the invoice was drafted — and totals them; nothing is recomputed here. The reads are a fixed
/// number of small queries (the lines join their PO line and PO in one), none per line.</para>
/// </summary>
public record GetSupplierInvoiceReportQuery(Guid InvoiceId) : IRequest<SupplierInvoiceReportDto>;

public class GetSupplierInvoiceReportQueryHandler : IRequestHandler<GetSupplierInvoiceReportQuery, SupplierInvoiceReportDto>
{
    private readonly IAppDbContext _db;
    private readonly IUnitDecimalsResolver _units;

    public GetSupplierInvoiceReportQueryHandler(IAppDbContext db, IUnitDecimalsResolver units)
    {
        _db = db;
        _units = units;
    }

    public async Task<SupplierInvoiceReportDto> Handle(GetSupplierInvoiceReportQuery request, CancellationToken ct)
    {
        var inv = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.InvoiceId, ct)
                  ?? throw new NotFoundException("Invoice", request.InvoiceId);

        var asn = inv.AsnId is { } asnId
            ? await _db.Asns.AsNoTracking().FirstOrDefaultAsync(a => a.Id == asnId, ct)
            : null;

        // Lines join their PO line and PO in one read (GetInvoiceByIdQuery's pattern); a stable PO / position order.
        var rows = await (from l in _db.InvoiceLines
                          join pol in _db.PurchaseOrderLines on l.PurchaseOrderLineId equals pol.Id
                          join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                          where l.InvoiceId == inv.Id
                          orderby po.PoNumber, pol.PositionNo, pol.SequenceNo
                          select new
                          {
                              PurchaseOrderLineId = pol.Id,
                              PoId = po.Id,
                              po.PoNumber,
                              PoPaymentTerms = po.PaymentTerms,
                              PoPaymentTermId = po.PaymentTermId,
                              PoDeliveryTerms = po.DeliveryTerms,
                              pol.PositionNo,
                              pol.SequenceNo,
                              pol.OrderUnit,
                              PoLineDiscountPct = pol.DiscountPct,
                              l.ItemCode,
                              l.ItemDescription,
                              l.BilledQty,
                              l.UnitPrice,
                              l.LineAmount,
                              LineDiscountPct = l.DiscountPct,
                              l.DiscountAmount,
                              l.TaxRatePct,
                              l.TaxAmount,
                          }).ToListAsync(ct);

        var supplierName = PackingListFormat.Blank(
            await _db.Suppliers.AsNoTracking().Where(s => s.Id == inv.SupplierId)
                .Select(s => s.LegalName).FirstOrDefaultAsync(ct));
        var parties = await PackingListParties.LoadSupplierAsync(_db, inv.SupplierId, supplierName, ct);

        // ── Covered POs: numbers, payment terms (text, else the master's description), delivery terms ──────────────
        var pos = rows.GroupBy(r => r.PoId).Select(g => g.First()).OrderBy(r => r.PoNumber).ToList();

        var termIds = pos.Where(p => string.IsNullOrWhiteSpace(p.PoPaymentTerms) && p.PoPaymentTermId is not null)
            .Select(p => p.PoPaymentTermId!.Value).Distinct().ToList();
        var termDescriptions = termIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.PaymentTerms.IgnoreQueryFilters().AsNoTracking()
                .Where(t => termIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Description, ct);

        var paymentTerms = pos
            .Select(p => PackingListFormat.Blank(p.PoPaymentTerms)
                         ?? (p.PoPaymentTermId is { } id ? PackingListFormat.Blank(termDescriptions.GetValueOrDefault(id)) : null))
            .OfType<string>().Distinct().ToList();

        var deliveryTerms = pos.Select(p => PackingListFormat.Blank(p.PoDeliveryTerms)).FirstOrDefault(t => t is not null)
                            ?? PackingListFormat.Blank(parties.DeliveryTermCode);

        // ── Parties: Bill To = the company + its base address; Ship To = company + receiving warehouse ──────────────
        var companyId = inv.TenantEntityId ?? asn?.TenantEntityId;
        var companyName = companyId is { } cid
            ? await _db.TenantEntities.AsNoTracking().Where(e => e.Id == cid).Select(e => e.Name).FirstOrDefaultAsync(ct)
            : null;
        IReadOnlyList<string> baseLines = companyId is { } bid
            ? (await PackingListParties.CompanyAddressAsync(_db, a => a.TenantEntityId == bid && a.IsBaseAddress, ct)).Lines
            : [];

        // The ASN's frozen warehouse first (exactly the packing list's source); an invoice with no ASN, or an ASN that
        // froze none, falls back to its first PO line's warehouse snapshot.
        var snapshot = asn?.WarehouseAddressSnapshot;
        var warehouseId = asn?.WarehouseAddressId;
        if (snapshot is null && warehouseId is null && rows.Count > 0)
        {
            var firstLineId = rows[0].PurchaseOrderLineId;
            var poLine = await _db.PurchaseOrderLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == firstLineId, ct);
            snapshot = poLine?.WarehouseAddressSnapshot;
            warehouseId = poLine?.WarehouseAddressId;
        }
        var (warehouseName, shipToLines) = await PackingListParties.ResolveWarehouseAsync(_db, snapshot, warehouseId, companyId, ct);

        // ── Lines ─────────────────────────────────────────────────────────────────────────────────────────────────
        var unitPlaces = await _units.ResolveAsync(rows.Select(r => r.OrderUnit), ct);

        var lines = rows.Select((r, i) => new SupplierInvoiceLineDto(
            SlNo: i + 1,
            PoNumber: r.PoNumber,
            PoLine: r.PositionNo,
            Position: null,
            Seq: r.SequenceNo,
            ItemCode: r.ItemCode,
            ItemDescription: r.ItemDescription,
            Qty: r.BilledQty,
            QtyDecimals: !string.IsNullOrWhiteSpace(r.OrderUnit) && unitPlaces.TryGetValue(r.OrderUnit, out var places)
                ? places : _units.Fallback,
            Uom: r.OrderUnit,
            UnitPrice: r.UnitPrice,
            DiscountPct: r.LineDiscountPct ?? (r.PoLineDiscountPct != 0m ? (decimal?)r.PoLineDiscountPct : null),
            DiscountAmount: r.DiscountAmount,
            TaxRatePct: r.TaxRatePct,
            TaxAmount: r.TaxAmount,
            LineTotal: r.LineAmount + r.TaxAmount)).ToList();

        var totalDiscount = rows.Sum(r => r.DiscountAmount);
        var totalTaxable = rows.Sum(r => r.LineAmount);
        var totalTax = rows.Sum(r => r.TaxAmount);
        var grandTotal = totalTaxable + totalTax;

        var driver = PackingListFormat.Pair(asn?.DriverName, asn?.DriverPhone);

        return new SupplierInvoiceReportDto(
            Supplier: parties.Supplier,
            BillTo: new PackingListPartyDto(PackingListFormat.Blank(companyName), baseLines, null, null, null, null),
            ShipTo: new PackingListPartyDto(PackingListFormat.Pair(companyName, warehouseName), shipToLines, null, null, null, null),
            ShipFrom: parties.ShipFrom,
            InvoiceNumber: inv.InvoiceNumber,
            InvoiceDate: inv.InvoiceDate,
            PurchaseOrders: PackingListFormat.Blank(string.Join(", ", pos.Select(p => p.PoNumber))),
            AsnNumber: asn?.AsnNumber,
            ShipmentNumber: asn?.ErpCode,
            GrnNumber: inv.GrnReference,
            Currency: inv.CurrencyCode,
            PaymentTerms: PackingListFormat.Blank(string.Join(", ", paymentTerms)),
            DeliveryTerms: deliveryTerms,
            SupplierCode: parties.SupplierCode,
            SupplierGstNo: parties.GstNumber,
            CustomerGstNo: null,
            AsnDate: asn?.CreatedOn,
            ExpectedDelivery: asn?.ExpectedDeliveryDate,
            Carrier: asn is null ? null : PackingListFormat.Carrier(asn.CarrierCode, asn.CarrierName),
            TrackingNo: asn?.TrackingNumber,
            BillOfLadingNo: asn?.BillOfLading,
            VehicleNo: asn?.VehicleNumber,
            Driver: driver,
            PackingListNo: asn?.PackingList,
            EwayBillNo: PackingListFormat.Blank(inv.EWayBillNumber) ?? asn?.EwayBillNo,
            EwayBillDate: asn?.EwayBillDate,
            IrnNo: PackingListFormat.Blank(inv.EInvoiceIrn) ?? asn?.IrnNo,
            IrnDate: asn?.IrnDate,
            Lines: lines,
            TotalDiscount: totalDiscount,
            TotalTaxable: totalTaxable,
            TotalTax: totalTax,
            GrandTotal: grandTotal,
            AmountInWords: AmountInWords.Format(grandTotal, inv.CurrencyCode),
            Notes: inv.Notes,
            GeneratedAt: AppTime.UtcNow);
    }
}
