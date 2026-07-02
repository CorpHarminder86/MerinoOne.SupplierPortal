using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

/// <summary>
/// R4 (2026-06-22) — Module 4. Returns the reshaped invoice detail: nullable header PO (multi-PO, Q1b), the
/// covered-PO list (derived from the distinct PO lines), the posting-lifecycle fields, IsLocked (Submitted+),
/// and RowVersion (base64) for the admin-revoke concurrency guard. The <c>?? Guid.Empty</c> shim is removed.
/// </summary>
public record GetInvoiceByIdQuery(Guid Id) : IRequest<InvoiceDetailDto>;

public class GetInvoiceByIdQueryHandler : IRequestHandler<GetInvoiceByIdQuery, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    public GetInvoiceByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<InvoiceDetailDto> Handle(GetInvoiceByIdQuery request, CancellationToken ct)
    {
        var inv = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                  ?? throw new NotFoundException("Invoice", request.Id);

        var supplier = await _db.Suppliers.Where(s => s.Id == inv.SupplierId)
            .Select(s => new { s.LegalName, s.SupplierCode }).FirstOrDefaultAsync(ct);

        // Ship-to for the PDF: the ASN's live ship-to (regular entity, loaded whole per GetPurchaseOrderByIdQuery
        // precedent) preferred over the header PO's point-in-time ShipTo snapshot (owned VO — same load-then-read
        // pattern as GetPurchaseOrderByIdQuery.cs, which avoids projecting owned types inside an anonymous Select).
        string? asnNumber = null;
        (string? Name, string? Line1, string? Line2, string? City, string? State, string? Pincode, string? Country)? shipTo = null;
        if (inv.AsnId.HasValue)
        {
            var asn = await _db.Asns.Include(a => a.ShipToAddress)
                .FirstOrDefaultAsync(a => a.Id == inv.AsnId.Value, ct);
            asnNumber = asn?.AsnNumber;
            if (asn?.ShipToAddress is { } sa)
                shipTo = (sa.AddressName, sa.AddressLine1, sa.AddressLine2, sa.City, sa.State, sa.Pincode, sa.Country);
        }
        if (shipTo is null && inv.PurchaseOrderId.HasValue)
        {
            var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == inv.PurchaseOrderId.Value, ct);
            if (po?.ShipTo is { } st)
                shipTo = (st.AddressName, st.Line1, st.Line2, st.City, st.State, st.Pincode, st.Country);
        }

        var isLocked = inv.InvoiceStatus != InvoiceStatus.Draft;

        // R6 — lines carry the frozen tax snapshot, the owning PO reference and the LIVE invoiceable balance
        // (RemainingQty = max(0, shippedQtyToDate − invoicedQtyToDate)) as the FE billedQty cap; 0 once locked.
        var lines = await (from l in _db.InvoiceLines
                           join pol in _db.PurchaseOrderLines on l.PurchaseOrderLineId equals pol.Id
                           join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                           where l.InvoiceId == request.Id
                           select new InvoiceLineDto(
                               l.Id, l.PurchaseOrderLineId, l.ItemCode, l.ItemDescription,
                               l.BilledQty, l.UnitPrice, l.LineAmount, l.TaxCode, l.TaxAmount,
                               l.TaxRatePct, l.TaxId, l.TaxDescription,
                               po.Id, po.PoNumber,
                               isLocked
                                   ? 0m
                                   : pol.ShippedQtyToDate - pol.InvoicedQtyToDate > 0
                                       ? pol.ShippedQtyToDate - pol.InvoicedQtyToDate
                                       : 0m))
            .ToListAsync(ct);

        // Covered POs = distinct PO of the invoice's lines (∪ legacy header PO for single-PO back-compat).
        var coveredPos = await (from il in _db.InvoiceLines
                                join pol in _db.PurchaseOrderLines on il.PurchaseOrderLineId equals pol.Id
                                join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                                where il.InvoiceId == request.Id
                                select new { po.Id, po.PoNumber })
                               .Distinct().ToListAsync(ct);
        var poList = coveredPos
            .Select(p => new InvoicePurchaseOrderDto(p.Id, p.PoNumber))
            .OrderBy(p => p.PoNumber)
            .ToList();

        string? headerPoNumber = null;
        if (inv.PurchaseOrderId.HasValue)
        {
            headerPoNumber = await _db.PurchaseOrders.Where(p => p.Id == inv.PurchaseOrderId.Value)
                .Select(p => p.PoNumber).FirstOrDefaultAsync(ct);
            if (headerPoNumber is not null && poList.All(p => p.PurchaseOrderId != inv.PurchaseOrderId.Value))
                poList.Insert(0, new InvoicePurchaseOrderDto(inv.PurchaseOrderId.Value, headerPoNumber));
        }

        var rowVersion = inv.RowVersion is { Length: > 0 } ? Convert.ToBase64String(inv.RowVersion) : null;

        return new InvoiceDetailDto(
            inv.Id, inv.Seq, inv.InvoiceNumber,
            inv.PurchaseOrderId, headerPoNumber, poList,
            inv.AsnId, asnNumber,
            inv.SupplierId, supplier?.LegalName ?? string.Empty, supplier?.SupplierCode ?? string.Empty,
            inv.InvoiceDate, inv.InvoiceAmount, inv.TaxAmount, inv.NetAmount,
            inv.CurrencyCode, inv.MatchingType.ToString(), inv.GrnReference,
            inv.InvoiceStatus.ToString(), inv.RejectionReason,
            inv.EInvoiceIrn, inv.EInvoiceAckNo, inv.EWayBillNumber,
            inv.SubmittedBy, inv.SubmittedAt, inv.ApprovedBy, inv.ApprovedAt,
            inv.RevokedBy, inv.RevokedAt, inv.RevokeReason,
            inv.ErpPostedAt, inv.ErpSyncId, inv.ErpCode,
            isLocked, rowVersion,
            inv.Notes, inv.CreatedOn, lines,
            inv.InvoiceOrigin.ToString(),
            shipTo?.Name, shipTo?.Line1, shipTo?.Line2, shipTo?.City, shipTo?.State, shipTo?.Pincode, shipTo?.Country);
    }
}
