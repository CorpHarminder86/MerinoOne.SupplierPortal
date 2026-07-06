using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.4 — reconciliation sweep, the SAFETY NET, D-R9-8) — low-frequency worker that catches
/// gate-eligible entities the event-driven enqueue missed (entities that became eligible without a
/// portal-side transition, e.g. SupplierSync which has no code enqueue site at all). Per gated config:
/// candidate filter narrows in SQL → gate per candidate → INSERT-IF-ABSENT only. The sweep NEVER
/// re-arms Skipped/Failed rows (Skipped = deliberately withdrawn; Failed = retry-managed) — that is
/// backfill's audited job. Runs under kill and under Held (enqueue never stops, D-R9-11); the dispatcher
/// side holds the actual POSTs. UQ races with a concurrent user enqueue are caught per row = AlreadyLive.
/// </summary>
internal sealed class LnGateReconciliationWorker : BackgroundService
{
    private const string IntervalConfigKey = "Integration:LnSweepIntervalMinutes";
    private const int DefaultIntervalMinutes = 60;
    private const string BatchSizeConfigKey = "Integration:LnSweepBatchSize";
    private const int DefaultBatchSize = 200;
    private const string SweepActor = "ln-gate-sweep";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LnGateReconciliationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public LnGateReconciliationWorker(IServiceScopeFactory scopeFactory, ILogger<LnGateReconciliationWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(int.TryParse(cfg[IntervalConfigKey], out var m) && m >= 1 ? m : DefaultIntervalMinutes);
        _batchSize = int.TryParse(cfg[BatchSizeConfigKey], out var b) && b >= 1 ? b : DefaultBatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LnGateReconciliationWorker started. Interval={Interval}min Batch={Batch}/config",
            _interval.TotalMinutes, _batchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepOnceAsync(_scopeFactory, _batchSize, _logger, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "LN gate reconciliation sweep failed.");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { /* shutdown */ }
        }
    }

    /// <summary>Testable core (IdmDocumentOutboxWorker.DrainOnceAsync precedent). Returns rows enqueued this pass.</summary>
    internal static async Task<int> SweepOnceAsync(IServiceScopeFactory scopeFactory, int batchSize, ILogger logger, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<ILnGateScanner>();

        // Sweep only configs that are gated (mode != Legacy + gate expr) AND have a candidate filter —
        // the SQL pre-filter is not optional (§2.4). Legacy configs keep their legacy code-eligibility.
        var configs = await db.LnEndpointConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => !c.IsDeleted
                        && c.DispatchMode != LnDispatchMode.Legacy
                        && c.EligibilityGateExpr != null && c.EligibilityGateExpr != ""
                        && c.CandidateFilterName != null && c.CandidateFilterName != "")
            .ToListAsync(ct);

        var enqueued = 0;
        foreach (var config in configs)
        {
            if (ct.IsCancellationRequested) break;
            IReadOnlyList<LnScanVerdict> verdicts;
            try
            {
                verdicts = await scanner.ScanAsync(config, batchSize, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[LnSweep] Scan failed for {Tx} (tenant {TenantId}).", config.TransactionType, config.TenantId);
                continue;
            }

            // INSERT-IF-ABSENT only: eligible + never enqueued. Skipped/Failed rows are backfill's job.
            foreach (var v in verdicts.Where(v => v.Eligible && v.ExistingRowStatus is null))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    // InvoicePost — reproduce the code-owned guard (c) claim VERBATIM before enqueue: the
                    // sweep must never enqueue an invoice a concurrent GRN cascade (or a prior sweep) already
                    // claimed, and a sweep-enqueued invoice must latch exactly like an event-enqueued one.
                    if (config.TransactionType == OutboxTransactionType.InvoicePost)
                    {
                        var claimedAt = DateTime.UtcNow;
                        var claimed = await db.Invoices
                            .IgnoreQueryFilters()
                            .Where(i => i.Id == v.EntityId
                                        && i.TenantId == v.TenantId
                                        && (i.InvoiceStatus == InvoiceStatus.Submitted || i.InvoiceStatus == InvoiceStatus.Matched)
                                        && i.ErpPostInitiatedAt == null)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(i => i.ErpPostInitiatedAt, claimedAt)
                                .SetProperty(i => i.ErpSyncId, v.DeterministicKey)
                                .SetProperty(i => i.UpdatedBy, SweepActor)
                                .SetProperty(i => i.UpdatedOn, claimedAt), ct);
                        if (claimed != 1) continue;
                    }

                    // Per-candidate scope: a unique-index race must poison ONLY its own change tracker,
                    // never the batch's.
                    using var rowScope = scopeFactory.CreateScope();
                    var rowDb = rowScope.ServiceProvider.GetRequiredService<IAppDbContext>();
                    var rowEnqueuer = rowScope.ServiceProvider.GetRequiredService<ILnGatedOutboxEnqueuer>();
                    var result = await rowEnqueuer.EnqueueAsync(
                        config.TransactionType, v.EntityName, v.EntityId, v.DeterministicKey, null,
                        tenantIdOverride: v.TenantId, ct: ct);
                    if (result.Outcome is GatedEnqueueOutcome.Enqueued or GatedEnqueueOutcome.EnqueuedLegacy or GatedEnqueueOutcome.Rearmed)
                    {
                        await rowDb.SaveChangesAsync(ct);
                        enqueued++;
                        logger.LogInformation("[LnSweep] Enqueued missed {Tx} {Entity}:{Id} key={Key}.",
                            config.TransactionType, v.EntityName, v.EntityId, v.DeterministicKey);
                    }
                }
                catch (DbUpdateException ex)
                {
                    // Unique-index race with a concurrent user-action enqueue — theirs won; ours is a no-op.
                    logger.LogInformation(ex, "[LnSweep] Key {Key} raced a concurrent enqueue — treated as AlreadyLive.", v.DeterministicKey);
                }
            }
        }
        return enqueued;
    }
}
