using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

/// <summary>
/// Post-commit outbound dispatcher (Increment 0). Drains <c>integration.OutboxMessage</c>: for each Pending row it
/// ATOMICALLY claims the row (<c>Pending → Sending</c>) BEFORE the ERP call, using the row's
/// <see cref="OutboxMessage.RowVersion"/> optimistic-concurrency token as the claim arbiter (review B1/D5), then —
/// only if it won the claim — calls the matching <see cref="IInforIntegrationService"/> method (replaying the row's
/// deterministic key as the ERP idempotency key via <see cref="IOutboundIdempotencyContext"/>), then —
/// <list type="bullet">
///   <item>on success: flips the row <c>Sending → Dispatched</c> and writes a Success outbound
///         <see cref="InforSyncLog"/> (<c>PayloadRef="&lt;Entity&gt;:&lt;guid&gt;"</c>). For an InvoicePost it also
///         promotes <c>Invoice.ErpPostInitiatedAt</c> → <c>ErpPostedAt</c> (review S2). The FINAL
///         <see cref="OutboxStatus.Acked"/> arrives later from <c>/inbound/erp-ack</c>;</item>
///   <item>on failure: flips the claimed row <c>Sending → Failed</c> (clears <c>dispatchedAt</c>,
///         leaves <c>Invoice.ErpPostedAt</c> null so a re-post is possible) and writes a retryable
///         <see cref="IntegrationError"/> (with <c>SyncLogId</c> pointing at the Failed SyncLog so
///         <c>RetryIntegrationErrorCommand</c> can re-arm it).</item>
/// </list>
///
/// <para><b>Crash-mid-POST recovery (review R1):</b> a crash AFTER the claim commits (row is <c>Sending</c>) but
/// BEFORE/DURING the ERP POST strands the row in <c>Sending</c>. The <see cref="SweepStaleSendingAsync"/> step at
/// the top of every drain resets any <c>Sending</c> row older than the configurable stale threshold
/// (<c>Integration:OutboxSendingStaleThresholdMinutes</c>, default 5 min) back to <c>Pending</c> so it is
/// re-dispatched. This is safe: the deterministic key is reused verbatim, so LN dedupes the re-POST per its own
/// business key (Q-LN). Without this, a crash-stranded invoice would read "never posted" with no recovery.</para>
///
/// CRITICAL invariant (fixes D1): the ERP HTTP call runs in this background scope, NEVER inside the caller's DB
/// transaction. CRITICAL (fixes D2): the deterministic key on the row is reused verbatim — never re-minted.
/// CRITICAL (fixes D3): every failure writes an <see cref="IntegrationError"/> + a <c>PayloadRef</c> so the retry
/// path is live for outbound. CRITICAL (review B1): the per-row claim is an atomic conditional
/// <c>ExecuteUpdateAsync</c> that flips <c>Pending → Sending</c> guarded by the row's <c>RowVersion</c> and only
/// POSTs when rowcount==1, so a restart, a second worker instance, or a poll overlap CANNOT double-POST — the claim
/// arbitrates, not the ERP's own dedup.
///
/// <para><b>NOT auto-post:</b> this drains rows that handlers explicitly enqueued (PO ack/accept/reject, ASN submit,
/// invoice submit). It does NOT itself trigger any new ERP transaction. With <c>Integration:Mode=Mock</c> (the
/// default) every dispatch is a deterministic mock OK.</para>
/// </summary>
internal sealed class OutboxDispatcherWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 25;

    /// <summary>Config key for the stale-<c>Sending</c> sweep threshold (minutes). Default 5.</summary>
    private const string StaleThresholdConfigKey = "Integration:OutboxSendingStaleThresholdMinutes";
    private const int DefaultStaleThresholdMinutes = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly TimeSpan _staleSendingThreshold;

    public OutboxDispatcherWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var minutes = int.TryParse(cfg[StaleThresholdConfigKey], out var m) && m >= 1
            ? m
            : DefaultStaleThresholdMinutes;
        _staleSendingThreshold = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxDispatcherWorker started. Poll={Poll}s Batch={Batch} StaleSendingSweep={Stale}min",
            PollInterval.TotalSeconds, BatchSize, _staleSendingThreshold.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "OutboxDispatcherWorker pump iteration failed.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutdown */ }
        }

        _logger.LogInformation("OutboxDispatcherWorker stopped.");
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // R1 — crash-mid-POST recovery FIRST: reset any stale Sending rows back to Pending so this same drain can
        // re-claim them below. A row left Sending by a crash between claim-commit and POST would otherwise never
        // auto-retry (the scan only re-selects Pending).
        await SweepStaleSendingAsync(db, ct);

        // IgnoreQueryFilters: this is a SYSTEM component draining EVERY tenant's outbox. The background scope has
        // no HttpContext so ICurrentUser.TenantId is null — the always-on tenant filter would otherwise strand
        // every message (OutboxMessage is ITenantOwned). Re-apply the soft-delete guard explicitly. We read only
        // the row id here: the authoritative Pending→Sending test is the ATOMIC CLAIM below, not this scan
        // (the scan can read a row a sibling instance is about to claim — that's fine, the claim arbitrates).
        var candidateIds = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.CreatedOn)
            .Take(BatchSize)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (candidateIds.Count == 0) return;

        foreach (var id in candidateIds)
        {
            if (ct.IsCancellationRequested) break;
            await DispatchOneAsync(scope.ServiceProvider, db, id, ct);
        }
    }

    /// <summary>
    /// R1 — stale-<c>Sending</c> sweep. A worker crash (or a forced shutdown) AFTER the claim commits the row to
    /// <c>Sending</c> but BEFORE the POST completes leaves the row stuck in <c>Sending</c> — the Pending scan never
    /// re-selects it and the failure path never ran, so absent this sweep the invoice would read "post initiated,
    /// never posted" with no recovery (review R1). This resets any <c>Sending</c> row whose last update is older
    /// than <see cref="_staleSendingThreshold"/> back to <c>Pending</c> (server-side conditional update), so the
    /// very next drain re-claims and re-POSTs it. Safe to re-POST: the deterministic key is reused verbatim, so LN
    /// dedupes per its own business key (Q-LN). Each reset is logged.
    /// </summary>
    private async Task SweepStaleSendingAsync(IAppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - _staleSendingThreshold;
        var now = DateTime.UtcNow;

        var reset = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted
                        && m.Status == OutboxStatus.Sending
                        // DispatchedAt is stamped at claim time; UpdatedOn is the audit fallback if it is ever null.
                        && (m.DispatchedAt ?? m.UpdatedOn ?? m.CreatedOn) < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                .SetProperty(m => m.LastError, "Re-armed by stale-Sending sweep (crash-mid-POST recovery).")
                .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                .SetProperty(m => m.UpdatedOn, now), ct);

        if (reset > 0)
            _logger.LogWarning("[Outbox] Stale-Sending sweep re-armed {Count} row(s) older than {Threshold}min back to Pending.",
                reset, _staleSendingThreshold.TotalMinutes);
    }

    /// <summary>
    /// Review B1/D5 — ATOMIC per-row claim BEFORE the ERP POST. The dispatcher conditionally flips the row
    /// <c>Pending → Sending</c> with a single <c>ExecuteUpdateAsync</c> guarded by BOTH the status predicate AND the
    /// row's loaded <see cref="OutboxMessage.RowVersion"/> token (server-side
    /// <c>UPDATE … SET status='Sending' WHERE Id=@id AND status='Pending' AND rowVersion=@rowVersion</c>); it
    /// proceeds to POST ONLY when the update affected exactly one row. A second instance, a restart, or a poll
    /// overlap that re-reads the same Pending row LOSES the claim (rowcount==0 — either the status moved or the
    /// rowVersion changed) and never POSTs — exactly-once dispatch without relying solely on the ERP's business-key
    /// dedup. On POST success the row flips <c>Sending → Dispatched</c>; on POST failure <c>Sending → Failed</c> + a
    /// retryable <see cref="IntegrationError"/>. A crash while the row is <c>Sending</c> is recovered by
    /// <see cref="SweepStaleSendingAsync"/>.
    /// </summary>
    private async Task DispatchOneAsync(IServiceProvider sp, IAppDbContext db, Guid rowId, CancellationToken ct)
    {
        // --- 0. Read the row (untracked) to capture the current RowVersion the claim will arbitrate on. ----------
        var row = await db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == rowId, ct);
        if (row is null || row.IsDeleted || row.Status != OutboxStatus.Pending) return;

        // --- 1. ATOMIC CLAIM (review B1/D5): Pending → Sending, server-side, gated by the RowVersion token. -------
        var claimRowVersion = row.RowVersion;
        var claimedAt = DateTime.UtcNow;
        var claimed = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Id == rowId
                        && m.Status == OutboxStatus.Pending
                        && !m.IsDeleted
                        && m.RowVersion == claimRowVersion)        // D5 — the 0023 rowVersion column now arbitrates the claim.
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Sending)
                .SetProperty(m => m.DispatchedAt, claimedAt)
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                .SetProperty(m => m.UpdatedOn, claimedAt), ct);

        // Lost the claim (another instance/poll flipped it out of Pending, or the rowVersion moved) → do NOT POST.
        // This is the crash/scale-out double-POST guard: only the winner of this conditional update reaches the ERP.
        if (claimed != 1) return;

        // Re-read the now-Sending row's payload (untracked) so the dispatcher can route + replay the deterministic
        // key. The AttemptCount reflects the committed increment from the claim.
        row = await db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == rowId, ct);
        if (row is null) return; // soft-deleted between claim and read — nothing to POST.

        // --- 2. POST (claim already won; the row is Sending and will not be re-POSTed by a sibling). --------------
        var infor = sp.GetRequiredService<IInforIntegrationService>();
        var idem = sp.GetRequiredService<IOutboundIdempotencyContext>();

        InforSyncResult result;
        try
        {
            // Replay the SAME deterministic key (D2 fix) as the ERP idempotency key.
            idem.Set(row.DeterministicKey);
            result = await InvokeAsync(infor, row, ct);
        }
        catch (Exception ex)
        {
            result = new InforSyncResult(false, row.DeterministicKey, ex.Message);
        }
        finally
        {
            idem.Clear();
        }

        var now = DateTime.UtcNow;
        var payloadRef = $"{row.EntityName}:{row.EntityId}";

        if (result.Success)
        {
            // Flip the claimed row Sending → Dispatched (the POST landed).
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == rowId && m.Status == OutboxStatus.Sending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Dispatched)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                    .SetProperty(m => m.UpdatedOn, now), ct);

            db.InforSyncLogs.Add(new InforSyncLog
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                EntityName = row.EntityName,
                EntityId = row.EntityId?.ToString(),
                Direction = SyncDirection.Outbound,
                Status = SyncStatus.Success,
                PayloadRef = payloadRef,
                IdempotencyKey = result.IdempotencyKey ?? row.DeterministicKey,
                SyncedAt = now,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = now,
            });
            await db.SaveChangesAsync(ct);

            // S2 — true ERP success: stamp Invoice.ErpPostedAt now (initiated→posted). A dispatch that never lands
            // leaves ErpPostedAt null so a future GRN re-approval (or manual retry) can re-post. Idempotent: only
            // sets the marker when still null.
            if (row.TransactionType == OutboxTransactionType.InvoicePost && row.EntityId is Guid invoiceId)
                await MarkInvoicePostedAsync(db, invoiceId, now, ct);

            _logger.LogInformation("[Outbox] Dispatched {Tx} {Entity}:{Id} key={Key}.",
                row.TransactionType, row.EntityName, row.EntityId, row.DeterministicKey);
        }
        else
        {
            var detail = string.IsNullOrEmpty(result.Message) ? "unknown ERP failure" : result.Message;

            // Roll the claimed row Sending → Failed (POST did not land). The atomic claim already incremented
            // AttemptCount, so the failed row carries its attempt number. A future RetryIntegrationErrorCommand
            // re-arms it (Failed → Pending) — or, for an invoice, a GRN re-approval (ErpPostedAt still null) re-posts.
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == rowId && m.Status == OutboxStatus.Sending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Failed)
                    .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, Truncate(detail, 2000))
                    .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                    .SetProperty(m => m.UpdatedOn, now), ct);

            var log = new InforSyncLog
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                EntityName = row.EntityName,
                EntityId = row.EntityId?.ToString(),
                Direction = SyncDirection.Outbound,
                Status = SyncStatus.Failed,
                PayloadRef = payloadRef,                // D3 fix — outbound retry resolves the target from here.
                IdempotencyKey = row.DeterministicKey,
                SyncedAt = now,
                ErrorMessage = Truncate(detail, 2000),
                RetryCount = row.AttemptCount,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = now,
            };
            db.InforSyncLogs.Add(log);

            db.IntegrationErrors.Add(new IntegrationError      // D3 fix — failures are now retryable.
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                SyncLogId = log.Id,
                EntityName = row.EntityName,
                ErrorMessage = Truncate(detail, 2000),
                RetryCount = row.AttemptCount,
                IsResolved = false,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = now,
            });
            await db.SaveChangesAsync(ct);

            _logger.LogWarning("[Outbox] Dispatch FAILED {Tx} {Entity}:{Id} key={Key} attempt={Attempt}: {Detail}",
                row.TransactionType, row.EntityName, row.EntityId, row.DeterministicKey, row.AttemptCount, detail);
        }
    }

    /// <summary>
    /// S2 — on confirmed ERP dispatch of an InvoicePost, promote the invoice from "post initiated"
    /// (<c>ErpPostInitiatedAt</c>, set at enqueue) to "posted" (<c>ErpPostedAt</c>). Server-side, idempotent
    /// (only stamps when still null), tenant/seccode-agnostic (the dispatcher is a system component) — keyed on
    /// the invoice id the outbox row carries. The complementary write-back also happens on <c>/inbound/erp-ack</c>
    /// for the InvoicePost; whichever lands first wins, the other is a no-op.
    /// </summary>
    private static async Task MarkInvoicePostedAsync(IAppDbContext db, Guid invoiceId, DateTime now, CancellationToken ct)
    {
        await db.Invoices
            .IgnoreQueryFilters()
            .Where(i => i.Id == invoiceId && i.ErpPostedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ErpPostedAt, now)
                .SetProperty(i => i.UpdatedBy, "outbox-dispatcher")
                .SetProperty(i => i.UpdatedOn, now), ct);
    }

    /// <summary>Routes the outbox row to the matching <see cref="IInforIntegrationService"/> method.</summary>
    private static async Task<InforSyncResult> InvokeAsync(IInforIntegrationService infor, OutboxMessage row, CancellationToken ct)
    {
        var id = row.EntityId ?? Guid.Empty;
        return row.TransactionType switch
        {
            OutboxTransactionType.PoAcknowledge  => await infor.AcknowledgePurchaseOrderAsync(id, ct),
            OutboxTransactionType.PoAccept        => await infor.AcceptPurchaseOrderAsync(id, ParseProposedDate(row.PayloadJson), ct),
            OutboxTransactionType.PoReject        => await infor.RejectPurchaseOrderAsync(id, ParseReason(row.PayloadJson), ct),
            OutboxTransactionType.AsnPost         => await infor.SubmitAsnAsync(id, ct),
            OutboxTransactionType.InvoicePost     => await infor.SubmitInvoiceAsync(id, ct),
            OutboxTransactionType.SupplierSync    => await infor.SyncSupplierAsync(id, ct),
            OutboxTransactionType.SupplierChange  => await infor.SubmitSupplierChangeAsync(id, ct),
            _ => new InforSyncResult(false, row.DeterministicKey,
                    $"No outbox dispatch route for transactionType '{row.TransactionType}'."),
        };
    }

    private static DateTime? ParseProposedDate(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("proposedDate", out var el) &&
                el.ValueKind == System.Text.Json.JsonValueKind.String &&
                DateTime.TryParse(el.GetString(), out var dt))
                return dt;
        }
        catch { /* malformed payload → treat as no proposed date */ }
        return null;
    }

    private static string ParseReason(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("reason", out var el) &&
                el.ValueKind == System.Text.Json.JsonValueKind.String)
                return el.GetString() ?? string.Empty;
        }
        catch { /* malformed payload → empty reason */ }
        return string.Empty;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
