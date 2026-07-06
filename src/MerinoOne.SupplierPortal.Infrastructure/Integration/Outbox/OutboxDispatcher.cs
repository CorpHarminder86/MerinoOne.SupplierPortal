using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

/// <summary>
/// Transactional-outbox enqueue (Increment 0). <see cref="EnqueueAsync"/> ADDS an <see cref="OutboxMessage"/>
/// row to the change tracker (status <c>Pending</c>) so it commits in the SAME <c>SaveChangesAsync</c> as the
/// caller's local state change — no ERP HTTP call ever holds a DB transaction open (fixes D1). The deterministic
/// key is reused verbatim across retries (fixes D2). The post-commit <see cref="OutboxDispatcherWorker"/> drains
/// Pending rows, calls the ERP method and writes the Success <see cref="InforSyncLog"/> / failure
/// <see cref="IntegrationError"/> (fixes D3).
///
/// <para><b>Idempotent enqueue:</b> the <c>UQ_OutboxMessage_deterministicKey</c> filtered-unique index backs
/// exactly-once. As defence in depth (and to avoid a tracked-duplicate <c>SaveChanges</c> failure within the
/// caller's own unit of work), this also checks the change tracker + a no-tracking DB probe before adding.</para>
///
/// Scoped: ctor-injected per request, runs inside the caller's <see cref="IAppDbContext"/> so the row participates
/// in the caller's transaction.
///
/// <para><b>R9 (TSD R9 §2.4):</b> this is now a thin façade over <see cref="LnGatedOutboxEnqueuer"/> — the ONE
/// enqueue chokepoint gained the config eligibility gate and re-arm-over-create (D-R9-10a) for every call site
/// at once. With no config row (or a Legacy one) the behaviour is exactly the pre-R9 enqueue; gate-ineligible
/// enqueues create NOTHING (the business change proceeds; the reconciliation sweep catches later eligibility).</para>
/// </summary>
public class OutboxDispatcher : IOutboxDispatcher
{
    private readonly ILnGatedOutboxEnqueuer _gated;

    public OutboxDispatcher(ILnGatedOutboxEnqueuer gated) => _gated = gated;

    public Task EnqueueAsync(
        string transactionType,
        string entityName,
        Guid? entityId,
        string deterministicKey,
        string? payloadJson,
        CancellationToken ct = default)
        => _gated.EnqueueAsync(transactionType, entityName, entityId, deterministicKey, payloadJson, ct: ct);
}
