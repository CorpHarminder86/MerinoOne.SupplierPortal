using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

/// <summary>
/// Post-commit outbound dispatcher (Increment 0). Drains <c>integration.OutboxMessage</c>: for each Pending row it
/// ATOMICALLY claims the row (<c>Pending → Dispatched</c>) BEFORE the ERP call, then — only if it won the claim —
/// calls the matching <see cref="IInforIntegrationService"/> method (replaying the row's deterministic key as the
/// ERP idempotency key via <see cref="IOutboundIdempotencyContext"/>), then —
/// <list type="bullet">
///   <item>on success: writes a Success outbound <see cref="InforSyncLog"/> (<c>PayloadRef="&lt;Entity&gt;:&lt;guid&gt;"</c>);
///         the row stays <see cref="OutboxStatus.Dispatched"/> (the claim already set it). For an InvoicePost it also
///         promotes <c>Invoice.ErpPostInitiatedAt</c> → <c>ErpPostedAt</c> (review S2). The FINAL
///         <see cref="OutboxStatus.Acked"/> arrives later from <c>/inbound/erp-ack</c>;</item>
///   <item>on failure: rolls the claimed row back to <see cref="OutboxStatus.Failed"/> (clears <c>dispatchedAt</c>,
///         leaves <c>Invoice.ErpPostedAt</c> null so a re-post is possible) and writes a retryable
///         <see cref="IntegrationError"/> (with <c>SyncLogId</c> pointing at the Failed SyncLog so
///         <c>RetryIntegrationErrorCommand</c> can replay it).</item>
/// </list>
///
/// CRITICAL invariant (fixes D1): the ERP HTTP call runs in this background scope, NEVER inside the caller's DB
/// transaction. CRITICAL (fixes D2): the deterministic key on the row is reused verbatim — never re-minted.
/// CRITICAL (fixes D3): every failure writes an <see cref="IntegrationError"/> + a <c>PayloadRef</c> so the retry
/// path is live for outbound. CRITICAL (review B1): the per-row claim is an atomic conditional
/// <c>ExecuteUpdateAsync</c> that flips <c>Pending → Dispatched</c> and only POSTs when rowcount==1, so a restart,
/// a second worker instance, or a poll overlap CANNOT double-POST — the claim arbitrates, not the ERP's own dedup.
///
/// <para><b>NOT auto-post:</b> this drains rows that handlers explicitly enqueued (PO ack/accept/reject, ASN submit,
/// invoice submit). It does NOT itself trigger any new ERP transaction — Live auto-post (Module 5) stays disabled
/// this turn. With <c>Integration:Mode=Mock</c> (the default) every dispatch is a deterministic mock OK.</para>
/// </summary>
internal sealed class OutboxDispatcherWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherWorker> _logger;

    public OutboxDispatcherWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxDispatcherWorker started. Poll={Poll}s Batch={Batch}",
            PollInterval.TotalSeconds, BatchSize);

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

        // IgnoreQueryFilters: this is a SYSTEM component draining EVERY tenant's outbox. The background scope has
        // no HttpContext so ICurrentUser.TenantId is null — the always-on tenant filter would otherwise strand
        // every message (OutboxMessage is ITenantOwned). Re-apply the soft-delete guard explicitly. We read only
        // the row id here: the authoritative Pending→Dispatched test is the ATOMIC CLAIM below, not this scan
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
    /// Review B1 — ATOMIC per-row claim BEFORE the ERP POST. The dispatcher conditionally flips the row
    /// <c>Pending → Dispatched</c> with a single <c>ExecuteUpdateAsync</c> (server-side
    /// <c>UPDATE … SET status='Dispatched' WHERE Id=@id AND status='Pending'</c>); it proceeds to POST ONLY when
    /// the update affected exactly one row. A second instance, a restart, or a poll overlap that re-reads the same
    /// Pending row LOSES the claim (rowcount==0) and never POSTs — exactly-once dispatch without relying solely on
    /// the ERP's business-key dedup. On POST failure the row is rolled back to <c>Failed</c> + a retryable
    /// <see cref="IntegrationError"/> is written; the failed row is re-armed for a manual
    /// <c>RetryIntegrationErrorCommand</c> replay.
    ///
    /// <para><b>Follow-up for solution-architect (NOT implemented here — needs an enum value, out of scope this
    /// turn):</b> there is no intermediate <c>Sending</c> status, so a crash in the narrow window AFTER the
    /// claim commits (row is <c>Dispatched</c>) but BEFORE/DURING the ERP POST leaves the row <c>Dispatched</c>
    /// without a confirmed POST — that single in-flight row will NOT auto-retry (it relies on the ERP ack / manual
    /// reconciliation). A dedicated <c>OutboxStatus.Sending</c> (claim = <c>Pending→Sending</c>, success =
    /// <c>Sending→Dispatched</c>, crash recovery = sweep stale <c>Sending</c> back to <c>Pending</c> by age) would
    /// close that window. The double-POST hole (the BLOCKER) is fully closed WITHOUT it; this is a
    /// crash-mid-POST recovery refinement.</para>
    /// </summary>
    private async Task DispatchOneAsync(IServiceProvider sp, IAppDbContext db, Guid rowId, CancellationToken ct)
    {
        // --- 1. ATOMIC CLAIM (review B1): Pending → Dispatched, server-side, before any ERP call. ----------------
        var claimedAt = DateTime.UtcNow;
        var claimed = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.Id == rowId && m.Status == OutboxStatus.Pending && !m.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Dispatched)
                .SetProperty(m => m.DispatchedAt, claimedAt)
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                .SetProperty(m => m.UpdatedOn, claimedAt), ct);

        // Lost the claim (another instance/poll already flipped it out of Pending) → do NOT POST. This is the
        // crash/scale-out double-POST guard: only the winner of this conditional update reaches the ERP.
        if (claimed != 1) return;

        // Re-read the claimed row's payload (untracked) so the dispatcher can route + replay the deterministic key.
        // It is now Dispatched; nobody else will POST it.
        var row = await db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == rowId, ct);
        if (row is null) return; // soft-deleted between claim and read — nothing to POST.

        // --- 2. POST (claim already won; the row will not be re-POSTed). -----------------------------------------
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
            // Row is already Dispatched from the claim. Write the Success SyncLog; clear any stale lastError.
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

            // Roll the claimed row back to Failed (POST did not land). The atomic claim already incremented
            // AttemptCount, so the failed row carries its attempt number. A future RetryIntegrationErrorCommand
            // (or, for an invoice, a GRN re-approval — ErpPostedAt is still null) can re-post.
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == rowId)
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
