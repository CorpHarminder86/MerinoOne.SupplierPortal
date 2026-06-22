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
/// Post-commit outbound dispatcher (Increment 0). Drains <c>integration.OutboxMessage</c>: claims <c>Pending</c>
/// rows, calls the matching <see cref="IInforIntegrationService"/> method (replaying the row's deterministic key
/// as the ERP idempotency key via <see cref="IOutboundIdempotencyContext"/>), then —
/// <list type="bullet">
///   <item>on success: writes a Success outbound <see cref="InforSyncLog"/> (<c>PayloadRef="&lt;Entity&gt;:&lt;guid&gt;"</c>)
///         and flips the row to <see cref="OutboxStatus.Dispatched"/>. The FINAL <see cref="OutboxStatus.Acked"/> arrives
///         later from the inbound <c>/inbound/erp-ack</c> endpoint (out of scope this turn — the status flow is designed for it);</item>
///   <item>on failure: writes a retryable <see cref="IntegrationError"/> (with <c>SyncLogId</c> pointing at the Failed
///         SyncLog so <c>RetryIntegrationErrorCommand</c> can replay it), increments <c>attemptCount</c> and sets
///         <see cref="OutboxStatus.Failed"/>.</item>
/// </list>
///
/// CRITICAL invariant (fixes D1): the ERP HTTP call runs in this background scope, NEVER inside the caller's DB
/// transaction. CRITICAL (fixes D2): the deterministic key on the row is reused verbatim — never re-minted.
/// CRITICAL (fixes D3): every failure writes an <see cref="IntegrationError"/> + a <c>PayloadRef</c> so the retry
/// path is live for outbound.
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
        // every message (OutboxMessage is ITenantOwned). Re-apply the soft-delete guard explicitly.
        var batch = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.Status == OutboxStatus.Pending)
            .OrderBy(m => m.CreatedOn)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        // Lease — flip to a transient claim so re-entry (multiple instances) won't double-dispatch. We re-use
        // Dispatched as the lease+terminal state; failures roll the row to Failed. (No partial "Sending" state
        // is needed: a row is either successfully Dispatched or marked Failed within this iteration.)
        foreach (var row in batch)
        {
            if (ct.IsCancellationRequested) break;
            await DispatchOneAsync(scope.ServiceProvider, db, row, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task DispatchOneAsync(IServiceProvider sp, IAppDbContext db, OutboxMessage row, CancellationToken ct)
    {
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

        row.AttemptCount += 1;
        row.UpdatedBy = "outbox-dispatcher";
        row.UpdatedOn = DateTime.UtcNow;

        var payloadRef = $"{row.EntityName}:{row.EntityId}";

        if (result.Success)
        {
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
                SyncedAt = DateTime.UtcNow,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = DateTime.UtcNow,
            });

            row.Status = OutboxStatus.Dispatched;
            row.DispatchedAt = DateTime.UtcNow;
            row.LastError = null;
            _logger.LogInformation("[Outbox] Dispatched {Tx} {Entity}:{Id} key={Key} attempt={Attempt}.",
                row.TransactionType, row.EntityName, row.EntityId, row.DeterministicKey, row.AttemptCount);
        }
        else
        {
            var detail = string.IsNullOrEmpty(result.Message) ? "unknown ERP failure" : result.Message;

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
                SyncedAt = DateTime.UtcNow,
                ErrorMessage = Truncate(detail, 2000),
                RetryCount = row.AttemptCount,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = DateTime.UtcNow,
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
                CreatedOn = DateTime.UtcNow,
            });

            row.Status = OutboxStatus.Failed;
            row.LastError = Truncate(detail, 2000);
            _logger.LogWarning("[Outbox] Dispatch FAILED {Tx} {Entity}:{Id} key={Key} attempt={Attempt}: {Detail}",
                row.TransactionType, row.EntityName, row.EntityId, row.DeterministicKey, row.AttemptCount, detail);
        }
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
