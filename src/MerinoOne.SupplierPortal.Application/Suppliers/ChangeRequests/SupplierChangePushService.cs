using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests;

/// <summary>
/// Per-line ERP push for an APPROVED change request (Module 2, plan §3/§4). Runs POST-COMMIT — after
/// <c>ApproveSupplierChangeRequestCommand</c> has applied the deltas + flipped the request to
/// <see cref="ChangeRequestStatus.Approved"/> in its own transaction. This service then:
/// <list type="number">
///   <item>enqueues the outbound push through the Increment-0 outbox (deterministic key, reused across retries,
///         doubling as the ERP correlation id / portalRef) — the post-commit <c>OutboxDispatcherWorker</c> performs
///         the actual <see cref="IInforIntegrationService.SubmitSupplierChangeAsync"/> call, which sends the FULL
///         intended end-state per erpCode-keyed entity (NOT a since-last delta);</item>
///   <item>marks each line's <see cref="SupplierChangeRequestLine.PushStatus"/> independently and stamps
///         <see cref="SupplierChangeRequestLine.PushedAt"/>; <c>ErpRef</c> is filled later by /inbound/erp-ack;</item>
///   <item>rolls the request header up to <see cref="ChangeRequestStatus.Pushed"/> (all lines pushed),
///         <see cref="ChangeRequestStatus.PartiallyPushed"/> (some) or <see cref="ChangeRequestStatus.PushFailed"/> (none);</item>
///   <item>on enqueue failure writes a retryable <see cref="IntegrationError"/> and — beyond a small retry
///         threshold — emits an operator-reconciliation signal (a distinct warning + IntegrationError; no new infra).</item>
/// </list>
///
/// <para><b>Dispatch granularity (decision):</b> we enqueue ONE outbox row for the whole change request
/// (<c>EntityId = changeRequestId</c>) so the existing dispatcher route — <c>SubmitSupplierChangeAsync(changeRequestId)</c>
/// — resolves and a single full-end-state push covers every erpCode-keyed entity in one ERP transaction (LN dedupes
/// on the deterministic key). Per-line <see cref="SupplierChangeRequestLine.PushStatus"/> is still tracked
/// independently: enqueue success marks every line <see cref="LinePushStatus.Pushed"/>; an enqueue failure marks
/// them <see cref="LinePushStatus.PushFailed"/>. Genuine per-entity partial outcomes are later refined by the
/// /inbound/erp-ack endpoint, which stamps each line's <c>ErpRef</c> as the ERP confirms it. This avoids N
/// redundant identical full-state pushes while keeping the per-line state model the diff view consumes.</para>
///
/// Scoped: ctor-injects the per-request <see cref="IAppDbContext"/> / <see cref="IOutboxDispatcher"/>.
/// </summary>
public sealed class SupplierChangePushService
{
    /// <summary>Beyond this many failed push attempts a reconciliation alert (operator signal) is raised.</summary>
    public const int ReconciliationThreshold = 3;

    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;
    private readonly ILogger<SupplierChangePushService> _logger;

    public SupplierChangePushService(IAppDbContext db, IOutboxDispatcher outbox, ILogger<SupplierChangePushService> logger)
    {
        _db = db;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>
    /// Pushes the approved <paramref name="request"/> (already tracked + loaded with its <c>Lines</c>). Stages the
    /// outbox row + per-line state + request rollup in the change tracker and SaveChanges ONCE so the push-state
    /// flip commits atomically with the outbox enqueue. The actual ERP call is post-commit (the dispatcher).
    /// </summary>
    public async Task PushAsync(SupplierChangeRequest request, string businessKey, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lines = request.Lines.Where(l => !l.IsDeleted).ToList();

        // Deterministic key — REUSED across retries so the ERP dedupes (doubles as the ERP correlation id / portalRef
        // echoed back on /inbound/erp-ack). NEVER a fresh GUID.
        var key = OutboxKey.For(OutboxEntity.SupplierChange, businessKey, "push");

        // Only (re)push lines NOT already confirmed Pushed — a line may have been confirmed by a prior
        // /inbound/erp-ack (per-line erpRef stamped) before a retry of this push. Those stay Pushed; the rest are
        // (re)attempted. This is what makes PartiallyPushed genuinely reachable on the rollup below.
        var toPush = lines.Where(l => l.PushStatus != LinePushStatus.Pushed).ToList();

        try
        {
            await _outbox.EnqueueAsync(
                OutboxTransactionType.SupplierChange,
                OutboxEntity.SupplierChange,
                request.Id,
                key,
                payloadJson: null,   // the dispatcher rebuilds the full end-state payload from the entity.
                ct);

            foreach (var line in toPush)
            {
                line.PushStatus = LinePushStatus.Pushed;
                line.PushedAt = now;
                line.UpdatedBy = "supplier-change-push";
                line.UpdatedOn = now;
            }
            _logger.LogInformation(
                "[SupplierChange] Enqueued push for change request {RequestId} ({Pushed}/{Total} lines) key={Key}.",
                request.Id, toPush.Count, lines.Count, key);
        }
        catch (Exception ex)
        {
            // Enqueue failed — the approval's applied portal deltas are NOT rolled back (they already committed in the
            // approve transaction). Mark the attempted lines failed (retryable) and raise the reconciliation signal if
            // we are beyond the threshold. The change request stays divergent-from-ERP and is visible as PushFailed.
            _logger.LogWarning(ex, "[SupplierChange] Push enqueue FAILED for change request {RequestId}.", request.Id);

            foreach (var line in toPush)
            {
                line.PushStatus = LinePushStatus.PushFailed;
                line.UpdatedBy = "supplier-change-push";
                line.UpdatedOn = now;
            }

            var failedCount = lines.Count(l => l.PushStatus == LinePushStatus.PushFailed);
            WriteIntegrationError(request, ex.Message, failedCount, now);

            if (failedCount >= ReconciliationThreshold)
            {
                _logger.LogError(
                    "[SupplierChange] RECONCILIATION ALERT — change request {RequestId} has {Failed} push failures " +
                    "(threshold {Threshold}). Portal supplier-master is ahead of ERP; operator action required.",
                    request.Id, failedCount, ReconciliationThreshold);
            }
        }

        // Roll the request header up from the per-line outcomes: all Pushed → Pushed; none Pushed → PushFailed;
        // a mix → PartiallyPushed. Reachable when a retry confirms some lines but not others (via /inbound/erp-ack).
        var pushed = lines.Count(l => l.PushStatus == LinePushStatus.Pushed);
        request.ChangeStatus = pushed == lines.Count
            ? ChangeRequestStatus.Pushed
            : pushed == 0 ? ChangeRequestStatus.PushFailed : ChangeRequestStatus.PartiallyPushed;
        request.UpdatedBy = "supplier-change-push";
        request.UpdatedOn = now;

        try { await _db.SaveChangesAsync(ct); }
        catch (Exception saveEx)
        {
            _logger.LogError(saveEx, "[SupplierChange] Failed to persist push state for {RequestId}.", request.Id);
        }
    }

    private void WriteIntegrationError(SupplierChangeRequest request, string detail, int retryCount, DateTime now)
    {
        _db.IntegrationErrors.Add(new IntegrationError
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            EntityName = OutboxEntity.SupplierChange,
            ErrorMessage = Truncate($"Supplier change push enqueue failed: {detail}", 2000),
            RetryCount = retryCount,
            IsResolved = false,
            CreatedBy = "supplier-change-push",
            CreatedOn = now,
        });
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
