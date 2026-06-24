namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IInforIntegrationService
{
    Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default);
    Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default);
    Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid purchaseOrderId, DateTime? proposedDate, CancellationToken ct = default);
    Task<InforSyncResult> RejectPurchaseOrderAsync(Guid purchaseOrderId, string reason, CancellationToken ct = default);
    Task<InforSyncResult> SubmitInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<InforSyncResult> SubmitAsnAsync(Guid asnId, CancellationToken ct = default);

    /// <summary>
    /// Pushes an approved supplier change-request to ERP (Module 2). The per-line push state + reconciliation
    /// is owned by the handler; this method performs the outbound call and returns the result. Backend-developer
    /// owns the Mock + Live implementations (Increment C).
    /// </summary>
    Task<InforSyncResult> SubmitSupplierChangeAsync(Guid changeRequestId, CancellationToken ct = default);

    /// <summary>
    /// R4 (2026-06-24) — pushes a buyer-APPROVED PO negotiation (revised qty / delivery dates) to ERP. The
    /// dispatcher routes the <c>PoNegotiationApprove</c> outbox row here; this method builds the canonical payload
    /// (via <c>PoNegotiationOutboundPayloadBuilder</c>) and performs the outbound call. The InforSyncLog write is
    /// owned by the <c>OutboxDispatcherWorker</c> on the result.
    /// </summary>
    Task<InforSyncResult> ApprovePoNegotiationAsync(Guid negotiationId, CancellationToken ct = default);
}

/// <summary>
/// Outbound transactional-outbox enqueue contract (Increment 0). Callers enqueue a row in the SAME
/// transaction as their local state change; a post-commit dispatcher (backend-developer) drains it,
/// calls the ERP method and writes the SyncLog / IntegrationError. The <paramref name="deterministicKey"/>
/// is reused verbatim across retries (NOT a fresh GUID) so ERP dedupes, and doubles as the ERP correlation
/// id / <c>portalRef</c> echoed back on <c>/inbound/erp-ack</c>. INTERFACE STUB ONLY — the implementation
/// (helper + background dispatcher) is a backend-developer hand-off.
/// </summary>
public interface IOutboxDispatcher
{
    Task EnqueueAsync(
        string transactionType,
        string entityName,
        Guid? entityId,
        string deterministicKey,
        string? payloadJson,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of an outbound ERP call. <paramref name="RequestPayloadJson"/> (additive, optional) carries the
/// canonical "what we sent" body the service built for the POST — both Mock and Live populate it so the
/// <see cref="OutboxDispatcherWorker"/> can persist it to <c>InforSyncLog.PayloadJson</c> and the SyncLog
/// payload viewer can render it. Null for routes that do not build a payload.
///
/// <para><paramref name="ErpCode"/> (additive, optional) is the SYNCHRONOUS-ack optimization (FIX #2 longer-term
/// primary fix): when the ERP returns the entity's assigned code INLINE in the POST response, the dispatcher
/// applies it (writes the code back + flips the outbox row straight to <c>Acked</c>) without waiting for the
/// async <c>/inbound/erp-ack</c> callback — closing the "Dispatched but never Acked" gap at the source. Null when
/// the ERP does not return a code inline (e.g. the Mock, or a fire-and-forget LN BOD that acks asynchronously),
/// in which case the row stays <c>Dispatched</c> and the async ack (or the reconciliation sweep) takes over.</para>
/// </summary>
public record InforSyncResult(
    bool Success,
    string? IdempotencyKey,
    string? Message,
    string? RequestPayloadJson = null,
    string? ErpCode = null);
