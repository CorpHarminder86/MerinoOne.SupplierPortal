using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
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

        var facts = await AsnOutboundFacts.LoadAsync(db, asn, ct);

        var lines = asn.Lines
            .Where(l => !l.IsDeleted)
            .OrderBy(l => l.PositionNo)
            .Select(l =>
            {
                var po = facts.PoLine(l.PurchaseOrderLineId);
                var serials = l.Serials.Where(s => !s.IsDeleted).ToList();
                var lots = l.Lots.Where(x => !x.IsDeleted).ToList();
                return new AsnLineInputDoc(
                    PoNumber: po?.PoNumber,
                    PoOrigin: po?.PoOrigin,
                    PositionNo: l.PositionNo,
                    SequenceNo: l.SequenceNo,
                    ItemCode: po?.ItemCode,
                    ShippedQty: l.ShippedQty,
                    // D5 — mirror, not a conversion.
                    ShippedQtyInvUnit: l.ShippedQty,
                    Uom: po?.OrderUnit,
                    BatchNumber: l.BatchNumber,
                    ExpiryDate: l.ExpiryDate?.ToString("o"),
                    Serials: serials.Count == 0
                        ? null
                        : serials.Select(s => new AsnSerialInputDoc(
                            s.SerialNumber,
                            AsnOutboundFacts.FormatDate(s.ExpiryDate))).ToList(),
                    Lots: lots.Count == 0
                        ? null
                        : lots.Select(lot => new AsnLotInputDoc(
                            lot.LotNo, lot.Qty, AsnOutboundFacts.FormatDate(lot.ExpiryDate))).ToList());
            })
            .ToList();

        var doc = new AsnInputDoc(
            Id: asn.Id,
            AsnNumber: asn.AsnNumber,
            CompanyCode: facts.CompanyCode,
            SupplierBp: facts.SupplierBp,
            ExpectedDeliveryDate: asn.ExpectedDeliveryDate.ToString("o"),
            TimeWindow: asn.TimeWindow,
            CarrierName: asn.CarrierName,
            TrackingNumber: asn.TrackingNumber,
            VehicleNumber: asn.VehicleNumber,
            DriverName: asn.DriverName,
            DriverPhone: asn.DriverPhone,
            Warehouse: asn.Warehouse,
            // R16 (2026-08-10) — both instants ship as seconds-precision UTC with an EXPLICIT Z, via the same
            // helper the PO_Update documents use. SQL Server hands these back as DateTimeKind.Unspecified while
            // the portal stores UTC, so the old ToString("o") emitted a naive "2026-08-10T07:17:29.2600214" and
            // left the timezone to LN's imagination — the R12 probe caught LN reading a naive PO date as UTC−05:00
            // and echoing it back +5 h, looking perfectly healthy. ExpectedDeliveryDate deliberately keeps its
            // naive "o" form: LN's own ASN sample shows it that way, so it is a business date, not an instant.
            CreateDate: PoLineDocumentAssembler.FormatUtc(asn.CreatedOn),
            ShipmentDate: PoLineDocumentAssembler.FormatUtc(asn.SubmittedAt),
            InvoiceNo: asn.InvoiceNo,
            BillOfLading: asn.BillOfLading,
            PackingList: asn.PackingList,
            Notes: asn.Notes,
            AsnStatus: asn.AsnStatus.ToString(),
            ErpCode: asn.ErpCode,
            Lines: lines);

        return LnJson.SerializeInputDoc(doc);
    }
}
