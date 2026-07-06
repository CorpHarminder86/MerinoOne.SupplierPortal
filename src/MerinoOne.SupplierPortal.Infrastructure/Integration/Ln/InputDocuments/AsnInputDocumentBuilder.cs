using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — ASN input document. Mirrors <see cref="Infor.AsnOutboundPayloadBuilder"/> exactly: line children
/// (serials + lots) loaded, per-line ItemCode resolved from the source PO line, serials/lots null (not
/// empty) when absent so request expressions can reproduce the legacy WhenWritingNull omission.
/// </summary>
public sealed class AsnInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.Asn;
    public string BuilderVersion => LnInputDocumentVersions.Asn;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var asn = await db.Asns
            .IgnoreQueryFilters()
            .Include(a => a.Lines).ThenInclude(l => l.Serials)
            .Include(a => a.Lines).ThenInclude(l => l.Lots)
            .FirstOrDefaultAsync(a => a.Id == entityId && !a.IsDeleted, ct);
        if (asn is null) return null;

        var poLineIds = asn.Lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        var itemCodeByPoLine = await db.PurchaseOrderLines
            .IgnoreQueryFilters()
            .Where(p => poLineIds.Contains(p.Id) && !p.IsDeleted)
            .Select(p => new { p.Id, p.ItemCode })
            .ToDictionaryAsync(p => p.Id, p => p.ItemCode, ct);

        var lines = asn.Lines
            .Where(l => !l.IsDeleted)
            .OrderBy(l => l.PositionNo)
            .Select(l =>
            {
                var serials = l.Serials.Where(s => !s.IsDeleted).Select(s => s.SerialNumber).ToList();
                var lots = l.Lots.Where(x => !x.IsDeleted).ToList();
                return new AsnLineInputDoc(
                    PositionNo: l.PositionNo,
                    SequenceNo: l.SequenceNo,
                    ItemCode: itemCodeByPoLine.TryGetValue(l.PurchaseOrderLineId, out var ic) ? ic : null,
                    ShippedQty: l.ShippedQty,
                    BatchNumber: l.BatchNumber,
                    ExpiryDate: l.ExpiryDate?.ToString("o"),
                    Serials: serials.Count == 0 ? null : serials,
                    Lots: lots.Count == 0
                        ? null
                        : lots.Select(lot => new AsnLotInputDoc(lot.LotNo, lot.Qty, lot.ExpiryDate?.ToString("yyyy-MM-dd"))).ToList());
            })
            .ToList();

        var doc = new AsnInputDoc(
            Id: asn.Id,
            AsnNumber: asn.AsnNumber,
            ExpectedDeliveryDate: asn.ExpectedDeliveryDate.ToString("o"),
            CarrierName: asn.CarrierName,
            TrackingNumber: asn.TrackingNumber,
            VehicleNumber: asn.VehicleNumber,
            AsnStatus: asn.AsnStatus.ToString(),
            ErpCode: asn.ErpCode,
            ErpCompany: asn.ErpCompany,
            ErpTransactionType: asn.ErpTransactionType,
            ErpDocumentNo: asn.ErpDocumentNo,
            Lines: lines);

        return LnJson.SerializeInputDoc(doc);
    }
}
