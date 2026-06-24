using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// R4 (2026-06-24) — shared builder for the outbound PO-negotiation→ERP request body. BOTH
/// <see cref="LiveInforIntegrationService"/> (for the HTTP POST body) and <see cref="MockInforIntegrationService"/>
/// (so dev gets the identical canonical "what we sent" payload) call this so the JSON persisted to
/// <c>InforSyncLog.PayloadJson</c> is byte-for-byte the same in Mock and Live. Mirrors
/// <see cref="SupplierOutboundPayloadBuilder"/>.
///
/// Pushes a buyer-APPROVED PO negotiation: the revised qty / delivery date per changed PO line. Each line carries
/// both the original snapshot and the negotiated value so the ERP can map the BOD however it needs. The PO line
/// rows are NOT mutated locally — this payload is what tells ERP to re-issue the revised PO inbound.
///
/// TODO (per-tenant Infor LN spec): the LN PO-negotiation BOD field map is a STARTER. Confirm with the Infor LN
/// team before enabling Mode=Live (see memory `infor-live-cutover-checklist`).
/// </summary>
internal static class PoNegotiationOutboundPayloadBuilder
{
    /// <summary>Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty fields.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the outbound negotiation payload — or <c>null</c> if the negotiation does not exist.
    /// The returned object is also what the Live service POSTs, so the two never drift.
    /// </summary>
    internal static async Task<string?> BuildJsonAsync(IAppDbContext db, Guid negotiationId, CancellationToken ct = default)
    {
        var payload = await BuildPayloadAsync(db, negotiationId, ct);
        return payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Builds the anonymous negotiation payload object (header + changed lines), or <c>null</c> when the
    /// negotiation is not found. Live serializes this for the POST body; the JSON is also persisted for the
    /// SyncLog viewer.
    /// </summary>
    internal static async Task<object?> BuildPayloadAsync(IAppDbContext db, Guid negotiationId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: this runs in the background OutboxDispatcher scope, which has NO ambient tenant/seccode
        // (the dispatcher reads everything with IgnoreQueryFilters), so the tenant/company global filters would
        // otherwise return null. We re-apply the soft-delete guard explicitly (root + children).
        var negotiation = await db.PurchaseOrderNegotiations
            .IgnoreQueryFilters()
            .Include(n => n.Lines)
            .FirstOrDefaultAsync(n => n.Id == negotiationId && !n.IsDeleted, ct);
        if (negotiation is null) return null;

        return new
        {
            PoNumber = negotiation.PoNumber,
            NegotiationId = negotiation.Id,
            SubmittedAt = negotiation.SubmittedAt.ToString("o"),
            Lines = negotiation.Lines
                .Where(l => !l.IsDeleted)
                .OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo)
                .Select(l => new
                {
                    positionNo = l.PositionNo,
                    sequenceNo = l.SequenceNo,
                    itemCode = l.ItemCode,
                    originalQty = l.OriginalQty,
                    negotiatedQty = l.NegotiatedQty,
                    originalDeliveryDate = l.OriginalDeliveryDate?.ToString("o"),
                    negotiatedDeliveryDate = l.NegotiatedDeliveryDate?.ToString("o"),
                })
                .ToList(),
        };
    }
}
