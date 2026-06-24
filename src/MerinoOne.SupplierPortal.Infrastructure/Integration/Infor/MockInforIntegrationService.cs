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
    private readonly IAppDbContext _db;

    public MockInforIntegrationService(IOutboundIdempotencyContext idempotency, IAppDbContext db)
    {
        _idempotency = idempotency;
        _db = db;
    }

    private InforSyncResult Ok(string action, Guid id, string? payloadJson = null)
        => new(true, _idempotency.CurrentKey ?? Guid.NewGuid().ToString("N"), $"[mock] {action} for {id}", payloadJson);

    public Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Ok("AckPO", id));
    public Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid id, DateTime? proposed, CancellationToken ct = default) => Task.FromResult(Ok("AcceptPO", id));
    public Task<InforSyncResult> RejectPurchaseOrderAsync(Guid id, string reason, CancellationToken ct = default) => Task.FromResult(Ok("RejectPO", id));

    // R4 (2026-06-23) — build the SAME canonical payload Live posts (via the shared builder) so dev/Mock submits land
    // a viewable InforSyncLog.PayloadJson the user can open and share with the LN team for field-map confirmation.
    public async Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default)
        => Ok("SyncSupplier", supplierId, await SupplierOutboundPayloadBuilder.BuildJsonAsync(_db, supplierId, ct));

    public async Task<InforSyncResult> SubmitInvoiceAsync(Guid id, CancellationToken ct = default)
        => Ok("SubmitInvoice", id, await InvoiceOutboundPayloadBuilder.BuildJsonAsync(_db, id, ct));

    public async Task<InforSyncResult> SubmitAsnAsync(Guid id, CancellationToken ct = default)
        => Ok("SubmitAsn", id, await AsnOutboundPayloadBuilder.BuildJsonAsync(_db, id, ct));

    // R4 Module 2 — supplier change-request push. Builds the SAME end-state payload Live posts (via the shared
    // builder); the dispatcher's per-line PushStatus rollup and the InforSyncLog (EntityName='SupplierChange',
    // PayloadRef='SupplierChange:<guid>') are written by the OutboxDispatcherWorker on this success.
    public async Task<InforSyncResult> SubmitSupplierChangeAsync(Guid id, CancellationToken ct = default)
        => Ok("SubmitSupplierChange", id, await SupplierChangeOutboundPayloadBuilder.BuildJsonAsync(_db, id, ct));

    // R4 (2026-06-24) — PO negotiation approve. Builds the SAME canonical payload Live posts (via the shared
    // builder) so dev/Mock approvals land a viewable InforSyncLog.PayloadJson (EntityName='PurchaseOrder',
    // PayloadRef='PurchaseOrder:<negotiationId>') the user can share with the LN team for BOD field-map confirmation.
    public async Task<InforSyncResult> ApprovePoNegotiationAsync(Guid id, CancellationToken ct = default)
        => Ok("ApprovePoNegotiation", id, await PoNegotiationOutboundPayloadBuilder.BuildJsonAsync(_db, id, ct));
}
