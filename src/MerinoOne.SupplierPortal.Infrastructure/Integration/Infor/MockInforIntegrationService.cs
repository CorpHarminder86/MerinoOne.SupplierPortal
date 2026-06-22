using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Deterministic Mock outbound Infor integration (Integration:Mode=Mock, the default). Every call returns OK. The
/// returned idempotency key echoes the ambient deterministic outbox key (<see cref="IOutboundIdempotencyContext"/>)
/// when present — so a retried mock dispatch correlates to the same key the real ERP would dedupe on, and the
/// SyncLog stays stable across retries. Only legacy direct calls with no ambient key fall back to a fresh GUID.
/// </summary>
public class MockInforIntegrationService : IInforIntegrationService
{
    private readonly IOutboundIdempotencyContext _idempotency;

    public MockInforIntegrationService(IOutboundIdempotencyContext idempotency) => _idempotency = idempotency;

    private InforSyncResult Ok(string action, Guid id)
        => new(true, _idempotency.CurrentKey ?? Guid.NewGuid().ToString("N"), $"[mock] {action} for {id}");

    public Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default) => Task.FromResult(Ok("SyncSupplier", supplierId));
    public Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("AckPO", id));
    public Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid id, DateTime? proposed, CancellationToken ct = default) => Task.FromResult(Ok("AcceptPO", id));
    public Task<InforSyncResult> RejectPurchaseOrderAsync(Guid id, string reason, CancellationToken ct = default) => Task.FromResult(Ok("RejectPO", id));
    public Task<InforSyncResult> SubmitInvoiceAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("SubmitInvoice", id));
    public Task<InforSyncResult> SubmitAsnAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("SubmitAsn", id));

    // R4 Module 2 — supplier change-request push. Deterministic OK; the dispatcher's per-line PushStatus rollup
    // and the InforSyncLog (EntityName='SupplierChange', PayloadRef='SupplierChange:<guid>') are written by the
    // OutboxDispatcherWorker on this success.
    public Task<InforSyncResult> SubmitSupplierChangeAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("SubmitSupplierChange", id));
}
