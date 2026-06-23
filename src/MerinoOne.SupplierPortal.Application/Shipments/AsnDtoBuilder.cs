using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R4 (2026-06-22) — Module 3. Builds the multi-PO-aware <see cref="AsnDetailDto"/> for the ASN commands/queries
/// (Create / Update / Submit / GetById). Centralises the covered-PO list (junction OR legacy scalar PO),
/// per-line PO context + position/sequence snapshot, the locked-on-submit flag, the linked draft-invoice id,
/// and the ASN attachment projection — so every handler returns an identical shape.
/// </summary>
public static class AsnDtoBuilder
{
    /// <summary>True once the ASN is past Draft — Update / attachment-mutation are rejected.</summary>
    public static bool IsLocked(AsnStatus status) => status != AsnStatus.Draft;

    public static async Task<AsnDetailDto> BuildAsync(IAppDbContext db, Guid asnId, CancellationToken ct)
    {
        var a = await db.Asns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == asnId, ct)
                ?? throw new Common.Exceptions.NotFoundException("Asn", asnId);

        var supplierName = await db.Suppliers.Where(s => s.Id == a.SupplierId)
            .Select(s => s.LegalName).FirstOrDefaultAsync(ct) ?? string.Empty;

        // Covered POs: union of the junction rows and the legacy scalar header PO (single-PO back-compat).
        var junctionPoIds = await db.AsnPurchaseOrders
            .Where(j => j.AsnId == asnId && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);

        var coveredPoIds = junctionPoIds.ToHashSet();
        if (a.PurchaseOrderId.HasValue) coveredPoIds.Add(a.PurchaseOrderId.Value);

        var poById = await db.PurchaseOrders
            .Where(p => coveredPoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PoNumber, p.CurrencyCode })
            .ToDictionaryAsync(p => p.Id, ct);

        var coveredPos = coveredPoIds
            .Select(id => poById.TryGetValue(id, out var p)
                ? new AsnPurchaseOrderDto(p.Id, p.PoNumber, p.CurrencyCode)
                : new AsnPurchaseOrderDto(id, "(unknown)", null))
            .OrderBy(p => p.PoNumber)
            .ToList();

        string? headerPoNumber = a.PurchaseOrderId.HasValue && poById.TryGetValue(a.PurchaseOrderId.Value, out var hp)
            ? hp.PoNumber : null;

        // Lines join their PO line for item/qty + owning PO number. Projected to an intermediate row first so the
        // serial/lot children (loaded separately below) can be merged in by line id without an N+1 per line.
        var lineRows = await (from al in db.AsnLines
                              join pol in db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                              join po in db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                              where al.AsnId == asnId
                              orderby po.PoNumber, pol.PositionNo
                              select new
                              {
                                  al.Id,
                                  al.PurchaseOrderLineId,
                                  pol.PurchaseOrderId,
                                  po.PoNumber,
                                  PoPositionNo = pol.PositionNo,
                                  al.PositionNo,
                                  al.SequenceNo,
                                  pol.ItemCode,
                                  pol.ItemDescription,
                                  pol.OrderUnit,
                                  pol.OrderQty,
                                  al.ShippedQty,
                                  al.BatchNumber,
                                  al.ExpiryDate,
                              }).ToListAsync(ct);

        // R4 (2026-06-23) — Serial/Lot capture children for these lines (so read/lock view + wizard reload show them).
        var asnLineIds = lineRows.Select(r => r.Id).ToList();
        var serialsByLine = (await db.AsnLineSerials.AsNoTracking()
                .Where(s => asnLineIds.Contains(s.AsnLineId))
                .OrderBy(s => s.SerialNumber)
                .Select(s => new { s.AsnLineId, s.SerialNumber })
                .ToListAsync(ct))
            .GroupBy(s => s.AsnLineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.SerialNumber).ToList());
        var lotsGrouped = (await db.AsnLineLots.AsNoTracking()
                .Where(l => asnLineIds.Contains(l.AsnLineId))
                .OrderBy(l => l.LotNo)
                .Select(l => new { l.AsnLineId, Dto = new AsnLineLotDto(l.LotNo, l.Qty, l.ExpiryDate, l.ErpCode) })
                .ToListAsync(ct))
            .GroupBy(l => l.AsnLineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AsnLineLotDto>)g.Select(x => x.Dto).ToList());

        var lines = lineRows.Select(r => new AsnLineDto(
                r.Id, r.PurchaseOrderLineId, r.PurchaseOrderId, r.PoNumber,
                r.PoPositionNo, r.PositionNo, r.SequenceNo,
                r.ItemCode, r.ItemDescription, r.OrderUnit, r.OrderQty,
                r.ShippedQty, r.BatchNumber, r.ExpiryDate,
                serialsByLine.TryGetValue(r.Id, out var sl) ? sl : null,
                lotsGrouped.TryGetValue(r.Id, out var ll) ? ll : null))
            .ToList();

        // The auto-created draft invoice (1:1 with the ASN via asnId).
        var draftInvoiceId = await db.Invoices
            .Where(i => i.AsnId == asnId && !i.IsDeleted)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);

        var attachments = await BuildAttachmentsAsync(db, asnId, ct);

        return new AsnDetailDto(
            a.Id, a.Seq, a.AsnNumber,
            a.PurchaseOrderId, headerPoNumber,
            coveredPos,
            a.SupplierId, supplierName,
            a.ExpectedDeliveryDate, a.TimeWindow,
            a.CarrierName, a.TrackingNumber,
            a.VehicleNumber, a.DriverName, a.DriverPhone,
            a.AsnStatus.ToString(), a.Notes,
            a.SubmittedAt, a.SubmittedBy, a.ErpSyncId, a.ErpCode,
            draftInvoiceId, IsLocked(a.AsnStatus),
            lines, attachments);
    }

    public static async Task<IReadOnlyList<DocumentAttachmentDto>> BuildAttachmentsAsync(
        IAppDbContext db, Guid asnId, CancellationToken ct)
    {
        return await db.DocumentUploads
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Asn && d.OwnerEntityId == asnId && !d.IsDeleted)
            .OrderBy(d => d.CreatedOn)
            .Select(d => new DocumentAttachmentDto(
                d.Id, d.FileName, d.MimeType, d.FileSizeKb * 1024L, d.CreatedOn,
                $"files/proxy/{d.Id}", $"files/proxy/{d.Id}"))
            .ToListAsync(ct);
    }
}
