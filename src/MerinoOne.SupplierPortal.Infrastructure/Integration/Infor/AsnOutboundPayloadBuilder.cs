using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Shared builder for the outbound ASN→ERP request body. BOTH <see cref="LiveInforIntegrationService"/> (for the
/// HTTP POST body) and <see cref="MockInforIntegrationService"/> (so dev gets the identical canonical "what we sent"
/// payload) call this so the JSON persisted to <c>InforSyncLog.PayloadJson</c> is byte-for-byte the same in Mock and
/// Live. The shape, field map, PO-line ItemCode join, and serializer options mirror exactly what the Live ASN post
/// builds — keep them in lock-step.
///
/// TODO (Q-LN-serial): the LN advance-shipment-notice field map (header + line-child "serials"/"lots"/"lotNo"/"qty")
/// is a STARTER — confirm with the Infor LN team before enabling Mode=Live.
/// </summary>
internal static class AsnOutboundPayloadBuilder
{
    /// <summary>
    /// Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty serials/lots so a line
    /// with no per-unit capture omits those arrays entirely (matching the Live contract).
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Loads the ASN with its line children (serials + lots), resolves each line's ItemCode from the source PO line,
    /// and returns the serialized outbound payload JSON — or <c>null</c> if the ASN does not exist. The returned
    /// object is also what the Live service POSTs, so the two never drift.
    /// </summary>
    internal static async Task<string?> BuildJsonAsync(IAppDbContext db, Guid asnId, CancellationToken ct = default)
    {
        var payload = await BuildPayloadAsync(db, asnId, ct);
        return payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Builds the anonymous payload object (header + lines[] with serials[]/lots[]) for the ASN, or <c>null</c> when
    /// the ASN is not found. Live serializes this for the POST body; the JSON is also persisted for the SyncLog viewer.
    /// </summary>
    internal static async Task<object?> BuildPayloadAsync(IAppDbContext db, Guid asnId, CancellationToken ct = default)
    {
        // R4 (2026-06-23) — load the line children (serials + lots) so the ASN post carries the per-line capture.
        // IgnoreQueryFilters: this runs in the background OutboxDispatcher scope, which has NO ambient tenant/seccode
        // (the dispatcher reads everything with IgnoreQueryFilters), so the tenant/company global filters would
        // otherwise return null. We re-apply the soft-delete guard explicitly (root + children) since it's dropped too.
        var asn = await db.Asns
            .IgnoreQueryFilters()
            .Include(a => a.Lines).ThenInclude(l => l.Serials)
            .Include(a => a.Lines).ThenInclude(l => l.Lots)
            .FirstOrDefaultAsync(a => a.Id == asnId && !a.IsDeleted, ct);
        if (asn is null) return null;

        // The ASN line carries no itemCode; resolve it from the source PO line (PurchaseOrderLineId → ItemCode).
        // IgnoreQueryFilters (+ re-apply !IsDeleted): this runs in the tenant-less OutboxDispatcher scope, and with
        // the scope gate now fail-CLOSED a filtered query could otherwise drop these rows → null ItemCode in the
        // outbound payload. Matches how the ASN root is loaded above.
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
                return new
                {
                    PositionNo = l.PositionNo,
                    SequenceNo = l.SequenceNo,
                    ItemCode = itemCodeByPoLine.TryGetValue(l.PurchaseOrderLineId, out var ic) ? ic : null,
                    ShippedQty = l.ShippedQty,
                    BatchNumber = l.BatchNumber,
                    ExpiryDate = l.ExpiryDate?.ToString("o"),
                    // OMIT (null → dropped by WhenWritingNull) when the line has no serials / no lots.
                    Serials = serials.Count == 0 ? null : serials,
                    Lots = lots.Count == 0 ? null : lots.Select(lot => new
                    {
                        LotNo = lot.LotNo,
                        Qty = lot.Qty,
                        ExpiryDate = lot.ExpiryDate?.ToString("yyyy-MM-dd"),
                    }).ToList(),
                };
            })
            .ToList();

        return new
        {
            AsnNumber = asn.AsnNumber,
            ExpectedDeliveryDate = asn.ExpectedDeliveryDate.ToString("o"),
            CarrierName = asn.CarrierName,
            TrackingNumber = asn.TrackingNumber,
            VehicleNumber = asn.VehicleNumber,
            Lines = lines,
        };
    }
}
