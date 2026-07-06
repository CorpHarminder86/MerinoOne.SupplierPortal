using System.Text.Json;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// R9 (extraction, behaviour-preserving) — the three PO-response payloads that historically lived inline in
/// <see cref="LiveInforIntegrationService"/>. Extracted so (a) Live keeps POSTing the identical shapes and
/// (b) the R9 byte-parity harness has a callable legacy source for the PO types (Mock never built these).
/// Shapes and serializer options are verbatim from the inline originals — keep in lock-step with the
/// LN field-map TODOs there.
/// </summary>
internal static class PoResponseOutboundPayloadBuilder
{
    /// <summary>Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty fields.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static object BuildAcknowledgePayload(PurchaseOrder po) => new
    {
        PoNumber = po.PoNumber,
        Action = "Acknowledge",
        AcknowledgedAt = (po.AcknowledgmentAt ?? DateTime.UtcNow).ToString("o"),
    };

    internal static object BuildAcceptPayload(PurchaseOrder po, DateTime? proposedDate) => new
    {
        PoNumber = po.PoNumber,
        Action = "Accept",
        ProposedDeliveryDate = proposedDate?.ToString("o"),
    };

    internal static object BuildRejectPayload(PurchaseOrder po, string reason) => new
    {
        PoNumber = po.PoNumber,
        Action = "Reject",
        Reason = reason,
    };

    internal static string BuildAcknowledgeJson(PurchaseOrder po)
        => JsonSerializer.Serialize(BuildAcknowledgePayload(po), JsonOpts);

    internal static string BuildAcceptJson(PurchaseOrder po, DateTime? proposedDate)
        => JsonSerializer.Serialize(BuildAcceptPayload(po, proposedDate), JsonOpts);

    internal static string BuildRejectJson(PurchaseOrder po, string reason)
        => JsonSerializer.Serialize(BuildRejectPayload(po, reason), JsonOpts);
}
