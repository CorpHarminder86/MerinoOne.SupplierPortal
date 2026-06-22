using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Commands;

/// <summary>
/// Retries the underlying Infor integration leg that produced an <see cref="IntegrationError"/>.
///
/// <para><b>Review R2 — outbox-backed retry RE-ARMS, it does not bypass.</b> When the originating
/// <see cref="InforSyncLog"/> correlates to a live <see cref="OutboxMessage"/> (same tenant + deterministic key —
/// the dispatcher writes <c>IdempotencyKey == DeterministicKey</c> on the Failed SyncLog it produces), the retry
/// re-arms that outbox row <c>Failed → Pending</c> (clears <c>LastError</c>) and lets the
/// <c>OutboxDispatcherWorker</c> re-POST it uniformly — so the SAME dispatch path that promotes
/// <c>Invoice.ErpPostInitiatedAt → ErpPostedAt</c> on success runs. The old behaviour (a direct
/// <c>SubmitInvoiceAsync</c> that never flipped the outbox row off <c>Failed</c> and never set
/// <c>ErpPostedAt</c>) left a successfully-retried invoice reading "never posted" forever — that is the bug this
/// fixes. The error is marked resolved (the dispatcher now owns the outcome) and <c>RetryCount</c>/<c>LastRetriedAt</c>
/// are stamped.</para>
///
/// <para><b>Legacy / non-outbox path.</b> An error with no correlating outbox row (e.g. an inbound-upsert failure
/// or a pre-outbox legacy error) falls back to the direct dispatch: re-invoke the
/// <see cref="IInforIntegrationService"/> by (<c>EntityName</c>, <c>payloadRef</c>), write a fresh
/// <see cref="InforSyncLog"/>, resolve the error only on success. The deterministic key is replayed verbatim
/// (D2) — never a fresh GUID.</para>
/// </summary>
public record RetryIntegrationErrorCommand(Guid ErrorId) : IRequest<Unit>;

public class RetryIntegrationErrorCommandHandler : IRequestHandler<RetryIntegrationErrorCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;
    private readonly IOutboundIdempotencyContext _idempotency;

    public RetryIntegrationErrorCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        IInforIntegrationService infor,
        IOutboundIdempotencyContext idempotency)
    {
        _db = db;
        _user = user;
        _infor = infor;
        _idempotency = idempotency;
    }

    public async Task<Unit> Handle(RetryIntegrationErrorCommand request, CancellationToken ct)
    {
        var err = await _db.IntegrationErrors.FirstOrDefaultAsync(x => x.Id == request.ErrorId, ct)
                  ?? throw new NotFoundException("IntegrationError", request.ErrorId);

        InforSyncLog? originating = null;
        if (err.SyncLogId.HasValue)
        {
            originating = await _db.InforSyncLogs.FirstOrDefaultAsync(s => s.Id == err.SyncLogId.Value, ct);
        }

        var entityName = !string.IsNullOrEmpty(err.EntityName)
            ? err.EntityName
            : originating?.EntityName ?? string.Empty;

        // Deterministic key (D2 fix): replay the ORIGINATING idempotency key verbatim — NEVER mint a fresh GUID
        // — so the ERP dedupes the retry. The outbox dispatcher stamps this key on the Failed SyncLog it writes,
        // so it doubles as the OutboxMessage.DeterministicKey we re-arm below.
        var deterministicKey = originating?.IdempotencyKey;
        var direction = originating?.Direction ?? SyncDirection.Outbound;

        // ── Review R2 — prefer RE-ARMING the outbox row over a direct bypass dispatch. ────────────────────────────
        // The dispatcher owns the InvoicePost (and every outbound) post; a retry that lands at LN MUST flow through
        // it so ErpPostInitiatedAt → ErpPostedAt is promoted. Resolve the originating Failed outbox row by
        // (TenantId, DeterministicKey) — the same composite the dispatcher/enqueue/ack use (review B2).
        if (!string.IsNullOrWhiteSpace(deterministicKey))
        {
            var tenantId = originating?.TenantId ?? err.TenantId ?? _user.TenantId;
            var rearmed = await TryRearmOutboxRowAsync(tenantId, deterministicKey!, ct);
            if (rearmed)
            {
                err.RetryCount += 1;
                err.LastRetriedAt = DateTime.UtcNow;
                // The dispatcher now owns the outcome: it will re-POST and, on success, write a fresh Success
                // SyncLog + promote ErpPostedAt; on failure it writes a NEW Failed SyncLog + IntegrationError. So
                // this error is resolved here (it has been handed back to the dispatcher).
                err.IsResolved = true;
                err.ResolutionNote =
                    $"Re-armed outbox row (Failed→Pending) for re-dispatch at {DateTime.UtcNow:O} by {_user.UserCode}; " +
                    "the dispatcher will re-POST and promote ErpPostedAt on success.";
                await _db.SaveChangesAsync(ct);
                return Unit.Value;
            }
        }

        // ── Legacy / non-outbox-backed path: direct dispatch (no correlating outbox row). ─────────────────────────
        var payloadRef = originating?.PayloadRef; // e.g. "supplier:<guid>" — used to pick the target row

        err.RetryCount += 1;
        err.LastRetriedAt = DateTime.UtcNow;

        InforSyncResult result;
        try
        {
            if (!string.IsNullOrEmpty(deterministicKey)) _idempotency.Set(deterministicKey);
            result = await DispatchAsync(entityName, payloadRef, ct);
        }
        catch (Exception ex)
        {
            result = new InforSyncResult(false, deterministicKey, ex.Message);
        }
        finally
        {
            _idempotency.Clear();
        }

        // EntityId: the single entity GUID encoded in the payloadRef ("<target>:<guid>"), when present.
        var entityId = TryParseGuidFromPayloadRef(payloadRef)?.ToString();

        var log = new InforSyncLog
        {
            EntityName    = entityName,
            EntityId      = entityId,
            Direction     = direction,
            Status        = result.Success ? SyncStatus.Success : SyncStatus.Failed,
            PayloadRef    = payloadRef,
            RetryCount    = err.RetryCount,
            // Reuse the deterministic key on the retry log row too, so retry history stays correlatable.
            IdempotencyKey = result.IdempotencyKey ?? deterministicKey ?? Guid.NewGuid().ToString("N"),
            SyncedAt      = DateTime.UtcNow,
            ErrorMessage  = result.Success ? null : result.Message
        };
        _db.InforSyncLogs.Add(log);

        if (result.Success)
        {
            err.IsResolved = true;
            err.ResolutionNote = $"Retried successfully at {DateTime.UtcNow:O} by {_user.UserCode}";
        }
        else
        {
            err.IsResolved = false;
            var detail = string.IsNullOrEmpty(result.Message) ? "unknown failure" : result.Message;
            err.ResolutionNote = $"Retry {err.RetryCount} failed at {DateTime.UtcNow:O} by {_user.UserCode}: {detail}";
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    /// <summary>
    /// Review R2 — re-arm a Failed (or, defensively, a stale Sending) outbox row back to <c>Pending</c> so the
    /// dispatcher re-POSTs it through the SAME path that promotes <c>ErpPostedAt</c>. Server-side conditional
    /// update keyed on (TenantId, DeterministicKey); clears <c>LastError</c>. Returns true when exactly one row was
    /// re-armed. An already-<c>Acked</c> or already-<c>Pending</c>/<c>Dispatched</c> row is NOT re-armed (returns
    /// false → the legacy direct path is skipped and the caller treats it as nothing-to-retry via that path too,
    /// but we still want a clean resolution: see below). Tenant context absent (legacy rows) ⇒ false.
    /// </summary>
    private async Task<bool> TryRearmOutboxRowAsync(Guid? tenantId, string deterministicKey, CancellationToken ct)
    {
        if (tenantId is null) return false;

        var now = DateTime.UtcNow;
        var rearmed = await _db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted
                        && m.TenantId == tenantId
                        && m.DeterministicKey == deterministicKey
                        && (m.Status == OutboxStatus.Failed || m.Status == OutboxStatus.Sending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.UpdatedBy, string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode)
                .SetProperty(m => m.UpdatedOn, now), ct);

        return rearmed >= 1;
    }

    /// <summary>
    /// Maps (<paramref name="entityName"/>, <paramref name="payloadRef"/>) to the right
    /// <see cref="IInforIntegrationService"/> call. <paramref name="payloadRef"/> follows the
    /// convention "&lt;target&gt;:&lt;guid&gt;" set by mock outbound writers. Falls back to a
    /// best-effort retry when payloadRef is missing or malformed.
    /// </summary>
    private async Task<InforSyncResult> DispatchAsync(string entityName, string? payloadRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entityName))
            return new InforSyncResult(false, null, "Cannot retry: original entity name is unknown.");

        var targetId = TryParseGuidFromPayloadRef(payloadRef);

        return (entityName, targetId) switch
        {
            ("Supplier", Guid id)        => await _infor.SyncSupplierAsync(id, ct),
            ("PurchaseOrder", Guid id)   => await _infor.AcknowledgePurchaseOrderAsync(id, ct),
            ("Invoice", Guid id)         => await _infor.SubmitInvoiceAsync(id, ct),
            ("Asn", Guid id)             => await _infor.SubmitAsnAsync(id, ct),
            ("SupplierChange", Guid id)  => await _infor.SubmitSupplierChangeAsync(id, ct),
            _                            => new InforSyncResult(false, null,
                                              $"No retry handler for entity '{entityName}'" +
                                              (payloadRef is null ? " (no payloadRef)." : "."))
        };
    }

    private static Guid? TryParseGuidFromPayloadRef(string? payloadRef)
    {
        if (string.IsNullOrEmpty(payloadRef)) return null;
        var idx = payloadRef.IndexOf(':');
        var tail = idx >= 0 ? payloadRef[(idx + 1)..] : payloadRef;
        return Guid.TryParse(tail, out var g) ? g : null;
    }
}
