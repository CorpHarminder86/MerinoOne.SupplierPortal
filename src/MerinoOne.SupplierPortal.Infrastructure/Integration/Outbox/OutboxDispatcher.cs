using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

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
/// </summary>
public class OutboxDispatcher : IOutboxDispatcher
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public OutboxDispatcher(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task EnqueueAsync(
        string transactionType,
        string entityName,
        Guid? entityId,
        string deterministicKey,
        string? payloadJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deterministicKey))
            throw new ArgumentException("deterministicKey is required (and must be reused across retries).", nameof(deterministicKey));

        // Review B2 — the enqueue-idempotency probe MUST be qualified by (TenantId, DeterministicKey) to match the
        // composite UQ_OutboxMessage_tenant_deterministicKey. A global (cross-tenant) probe would treat tenant A's
        // outbox row as already-owning tenant B's identically-keyed post and silently no-op it (B marked posted with
        // NO outbox row → never paid). The deterministic key is already tenant-folded (OutboxKey.For), but the row's
        // TenantId is stamped by the interceptor from the same principal, so probe on it explicitly.
        var tenantId = _user.TenantId;

        // Already staged in THIS unit of work? Adding it twice would blow the SaveChanges on the composite unique index.
        var stagedLocally = _db.OutboxMessages.Local
            .Any(m => m.TenantId == tenantId && m.DeterministicKey == deterministicKey && !m.IsDeleted);
        if (stagedLocally) return;

        // Already persisted (a prior committed enqueue for the same tenant + business key/op)? No-op — the dispatcher
        // (or a later retry) already owns it. IgnoreQueryFilters: the outbox is system infrastructure; a background
        // dispatch scope has no tenant context, so the always-on tenant filter must not hide it.
        var alreadyPersisted = await _db.OutboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.DeterministicKey == deterministicKey && !m.IsDeleted, ct);
        if (alreadyPersisted) return;

        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TransactionType = transactionType,
            EntityName = entityName,
            EntityId = entityId,
            DeterministicKey = deterministicKey,
            PayloadJson = payloadJson,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            // TenantId stamped by ScopeStampInterceptor from the request principal (ITenantOwned).
            CreatedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        });
    }
}
