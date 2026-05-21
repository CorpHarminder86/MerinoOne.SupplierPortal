namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IInforIntegrationService
{
    Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default);
    Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default);
    Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid purchaseOrderId, DateTime? proposedDate, CancellationToken ct = default);
    Task<InforSyncResult> RejectPurchaseOrderAsync(Guid purchaseOrderId, string reason, CancellationToken ct = default);
    Task<InforSyncResult> SubmitInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<InforSyncResult> SubmitAsnAsync(Guid asnId, CancellationToken ct = default);
}

public record InforSyncResult(bool Success, string? IdempotencyKey, string? Message);
