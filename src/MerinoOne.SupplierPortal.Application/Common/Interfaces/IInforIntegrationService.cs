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

public record InforSyncResult(bool Success, string? IdempotencyKey, string? Message);
