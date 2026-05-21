using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

public class MockInforIntegrationService : IInforIntegrationService
{
    private static InforSyncResult Ok(string action, Guid id) => new(true, Guid.NewGuid().ToString("N"), $"[mock] {action} for {id}");

    public Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default) => Task.FromResult(Ok("SyncSupplier", supplierId));
    public Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("AckPO", id));
    public Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid id, DateTime? proposed, CancellationToken ct = default) => Task.FromResult(Ok("AcceptPO", id));
    public Task<InforSyncResult> RejectPurchaseOrderAsync(Guid id, string reason, CancellationToken ct = default) => Task.FromResult(Ok("RejectPO", id));
    public Task<InforSyncResult> SubmitInvoiceAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("SubmitInvoice", id));
    public Task<InforSyncResult> SubmitAsnAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("SubmitAsn", id));
}
