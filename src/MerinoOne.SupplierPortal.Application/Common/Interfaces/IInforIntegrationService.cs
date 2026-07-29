namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// The COMPILED outbound surface — one method per transaction type that still has a code-owned payload
/// builder.
///
/// <para><b>R12 (D13) — the four PO methods were removed.</b> <c>PoAccept</c>, <c>PoReject</c> and
/// <c>PoNegotiationApprove</c> are Dynamic-only now: their bodies come from
/// <c>OutboundIntegrationConfig.requestMappingExpr</c> through <c>LnDynamicDispatcher</c>, so a wire-literal
/// correction is a per-tenant config edit rather than a deploy. <c>PoAcknowledge</c> no longer posts to LN at
/// all (D14) — the portal Acknowledge action is unchanged, it just stops enqueuing.</para>
///
/// <para>Do not re-add a PO method here to "fix" a failing row. A PO row that cannot dispatch means its config
/// is still Legacy; the fix is to attest it and set <c>dispatchMode = Dynamic</c>, then re-arm.</para>
/// </summary>
public interface IInforIntegrationService
{
    Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default);
    Task<InforSyncResult> SubmitInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<InforSyncResult> SubmitAsnAsync(Guid asnId, CancellationToken ct = default);

    /// <summary>
    /// Pushes an approved supplier change-request to ERP (Module 2). The per-line push state + reconciliation
    /// is owned by the handler; this method performs the outbound call and returns the result. Backend-developer
    /// owns the Mock + Live implementations (Increment C).
    /// </summary>
    Task<InforSyncResult> SubmitSupplierChangeAsync(Guid changeRequestId, CancellationToken ct = default);
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
