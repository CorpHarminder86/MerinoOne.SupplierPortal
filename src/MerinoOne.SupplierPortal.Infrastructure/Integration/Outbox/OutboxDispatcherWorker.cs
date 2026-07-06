using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
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

    /// <summary>
    /// FIX #2 — config key for the stale-<c>Dispatched</c> (POST landed, no ERP ack) reconciliation threshold
    /// (minutes). Default 60.
    /// </summary>
    private const string DispatchedStaleThresholdConfigKey = "Integration:OutboxDispatchedStaleThresholdMinutes";
    private const int DefaultDispatchedStaleThresholdMinutes = 60;

    /// <summary>Stable reason marker that de-dupes the stale-Dispatched alert (keyed off the existing IntegrationError).</summary>
    private const string DispatchedReconcileReason = "outbox-dispatched-no-ack";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly TimeSpan _staleSendingThreshold;
    private readonly TimeSpan _staleDispatchedThreshold;

    public OutboxDispatcherWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var minutes = int.TryParse(cfg[StaleThresholdConfigKey], out var m) && m >= 1
            ? m
            : DefaultStaleThresholdMinutes;
        _staleSendingThreshold = TimeSpan.FromMinutes(minutes);

        var dispatchedMinutes = int.TryParse(cfg[DispatchedStaleThresholdConfigKey], out var dm) && dm >= 1
            ? dm
            : DefaultDispatchedStaleThresholdMinutes;
        _staleDispatchedThreshold = TimeSpan.FromMinutes(dispatchedMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxDispatcherWorker started. Poll={Poll}s Batch={Batch} StaleSendingSweep={Stale}min StaleDispatchedSweep={Dispatched}min",
            PollInterval.TotalSeconds, BatchSize, _staleSendingThreshold.TotalMinutes, _staleDispatchedThreshold.TotalMinutes);

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

    /// <summary>Internal (not private) so R9 routing tests can drive ONE deterministic drain without the hosted loop.</summary>
    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // R1 — crash-mid-POST recovery FIRST: reset any stale Sending rows back to Pending so this same drain can
        // re-claim them below. A row left Sending by a crash between claim-commit and POST would otherwise never
        // auto-retry (the scan only re-selects Pending).
        await SweepStaleSendingAsync(db, ct);

        // FIX #2 — stranded-Dispatched reconciliation: a row that POSTed successfully (Dispatched) but whose ERP ack
        // never arrived sits Dispatched forever with no operator visibility. Raise a ONE-TIME IntegrationError for
        // each such row past the configured threshold. Alert-only (no auto-resend — the POST already landed).
        await SweepStaleDispatchedAsync(db, ct);

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

        // R9 (D-R9-11) — ONE kill-switch read per drain cycle: tenants whose OutboundGlobal switch is off are
        // excluded from dispatch entirely (their rows stay Pending and accumulate; enqueue is untouched —
        // killing enqueue silently loses business events). Absent row = enabled.
        var killedTenants = (await db.IntegrationSwitches
                .IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && !s.IsEnabled && s.Scope == IntegrationSwitchScope.OutboundGlobal)
                .Select(s => s.TenantId)
                .ToListAsync(ct))
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .ToHashSet();

        // R9 (D-R9-2) — ONE config read per drain cycle: the tri-state routing map (tenant, transactionType) →
        // Legacy | Dynamic | Held. Config staleness is bounded by the poll interval (≤5 s), which is the
        // documented reaction time for a Held/kill flip. Empty table ⇒ empty map ⇒ 100% legacy dispatch.
        var routes = (await db.LnEndpointConfigs
                .IgnoreQueryFilters()
                .Where(c => !c.IsDeleted)
                .Select(c => new LnEndpointRoute(c.TenantId, c.TransactionType, c.DispatchMode, c.PortalEntity,
                    c.EndpointPath, c.HttpVerb, c.RequestMappingExpr, c.ResponseMappingExpr))
                .ToListAsync(ct))
            .ToDictionary(r => (r.TenantId, r.TransactionType));

        foreach (var id in candidateIds)
        {
            if (ct.IsCancellationRequested) break;
            await DispatchOneAsync(scope.ServiceProvider, db, id, routes, killedTenants, ct);
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
    /// FIX #2 — stranded-<c>Dispatched</c> reconciliation sweep. Mirrors <see cref="SweepStaleSendingAsync"/> but for
    /// the ASYNC-ack gap: a row whose POST genuinely landed (flipped <c>Sending → Dispatched</c>) but whose
    /// <c>/inbound/erp-ack</c> callback NEVER arrived sits <c>Dispatched</c> (and never reaches the terminal
    /// <see cref="OutboxStatus.Acked"/>) forever, invisible to operators. This finds every <c>Dispatched</c> row
    /// (NOT yet <c>Acked</c>, <see cref="OutboxMessage.AckedAt"/> still null) older than the configurable
    /// <see cref="_staleDispatchedThreshold"/> (<c>Integration:OutboxDispatchedStaleThresholdMinutes</c>, default 60)
    /// that has NOT already been alerted, and raises EXACTLY ONE retryable <see cref="IntegrationError"/> per row
    /// ("landed but no ERP ack — reconcile"). De-dupe uses the least-invasive existing column: a Dispatched success
    /// clears <see cref="OutboxMessage.LastError"/> to null, so <c>LastError == null</c> marks "not yet alerted"; the
    /// sweep stamps a stable marker after raising the alert so the next sweep skips the row.
    ///
    /// <para><b>NO auto-resend</b> (deliberate): the POST already landed, so a re-send risks a double-post — LN dedupes
    /// on its business key, but alert-only is the safer reconciliation posture. An operator (or the eventual ack)
    /// resolves the row.</para>
    /// </summary>
    private Task SweepStaleDispatchedAsync(IAppDbContext db, CancellationToken ct)
        => ReconcileStaleDispatchedAsync(db, _staleDispatchedThreshold, BatchSize, _logger, ct);

    /// <summary>
    /// FIX #2 — the testable core of the stranded-<c>Dispatched</c> reconciliation. Extracted as <c>internal static</c>
    /// so a focused test can drive it directly (no hosted background loop / no fixed wall-clock wait). Returns the
    /// number of IntegrationErrors raised this pass. Idempotent across passes: the per-row <see cref="OutboxMessage.LastError"/>
    /// marker stamped after the first alert excludes the row on subsequent passes (the "not re-alerted" guarantee).
    /// </summary>
    internal static async Task<int> ReconcileStaleDispatchedAsync(
        IAppDbContext db, TimeSpan threshold, int batchSize, ILogger logger, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - threshold;
        var now = DateTime.UtcNow;

        // Read the stranded rows (untracked) — only those not yet alerted (LastError null) past the threshold.
        var stranded = await db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted
                        && m.Status == OutboxStatus.Dispatched
                        && m.AckedAt == null
                        && m.LastError == null
                        && (m.DispatchedAt ?? m.UpdatedOn ?? m.CreatedOn) < cutoff)
            .Select(m => new
            {
                m.Id, m.TenantId, m.EntityName, m.EntityId, m.TransactionType, m.AttemptCount,
                m.DispatchedAt, m.UpdatedOn, m.CreatedOn
            })
            .Take(batchSize)
            .ToListAsync(ct);

        if (stranded.Count == 0) return 0;

        var raised = 0;
        foreach (var row in stranded)
        {
            if (ct.IsCancellationRequested) break;

            var landedAt = row.DispatchedAt ?? row.UpdatedOn ?? row.CreatedOn;
            var minutes = (int)Math.Round((now - landedAt).TotalMinutes);

            // ONE IntegrationError per stranded row (retryable, unresolved) so it surfaces in the operator UI.
            db.IntegrationErrors.Add(new IntegrationError
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                EntityName = row.EntityName,
                ErrorMessage =
                    $"Outbound {row.TransactionType} {row.EntityName}:{row.EntityId} landed but no ERP ack after {minutes} min — reconcile.",
                StackTrace = DispatchedReconcileReason,   // stable reason marker (de-dupe / classification handle).
                RetryCount = row.AttemptCount,            // carry the attempt count through.
                IsResolved = false,
                CreatedBy = "outbox-dispatcher",
                CreatedOn = now,
            });

            // Mark the row so it is NOT re-alerted on the next sweep (least-invasive: reuse the existing LastError
            // column, which a Dispatched success cleared to null). Server-side conditional update guarded on the
            // still-Dispatched + still-unalerted state so a concurrent ack (Dispatched → Acked) cannot be clobbered.
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == row.Id && m.Status == OutboxStatus.Dispatched && m.AckedAt == null && m.LastError == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.LastError, Truncate($"{DispatchedReconcileReason}: alerted at {now:O} after {minutes} min.", 2000))
                    .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                    .SetProperty(m => m.UpdatedOn, now), ct);

            raised++;
            logger.LogWarning(
                "[Outbox] Stale-Dispatched reconcile: {Tx} {Entity}:{Id} landed but no ERP ack after {Minutes}min — raised IntegrationError.",
                row.TransactionType, row.EntityName, row.EntityId, minutes);
        }

        await db.SaveChangesAsync(ct);
        return raised;
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
    private async Task DispatchOneAsync(
        IServiceProvider sp, IAppDbContext db, Guid rowId,
        IReadOnlyDictionary<(Guid? TenantId, string TransactionType), LnEndpointRoute> routes,
        IReadOnlySet<Guid> killedTenants, CancellationToken ct)
    {
        // --- 0. Read the row (untracked) to capture the current RowVersion the claim will arbitrate on. ----------
        var row = await db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == rowId, ct);
        if (row is null || row.IsDeleted || row.Status != OutboxStatus.Pending) return;

        // --- 0a. R9 (D-R9-11) — kill switches. Global-outbound kill (tenant scope) and the per-endpoint Held
        // mode both mean the row is NEVER claimed (stays Pending, FIFO preserved) and drains once re-enabled.
        // Enqueue is untouched by design — killing enqueue would silently lose business events.
        if (row.TenantId is { } rowTenant && killedTenants.Contains(rowTenant)) return;
        routes.TryGetValue((row.TenantId, row.TransactionType), out var route);
        if (route?.Mode == LnDispatchMode.Held) return;

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

        // --- 1a. R9 (D-R9-9) — dispatch-time gate RE-CHECK on the claimed row, the final guard before the POST.
        // Covers revoke-between-enqueue-and-dispatch and backfill races: a gate that now says no flips the row
        // Sending → Skipped (terminal, reason + gateVersion stamped) — NOT Failed, NO IntegrationError, NO LN
        // call (a skip is a decision, not a failure). Gate evaluation ERRORS also land Skipped (fail closed);
        // the outbox monitor is the surface for those.
        if (route?.Mode is LnDispatchMode.Dynamic or LnDispatchMode.Held && row.TenantId is { } gateTenant && row.EntityId is { } gateEntity)
        {
            var verdict = await sp.GetRequiredService<Application.Integration.Ln.ILnEligibilityService>()
                .EvaluateAsync(gateTenant, row.TransactionType, gateEntity, null, ct);
            if (verdict.HasGate && !verdict.Eligible)
            {
                var skippedAt = DateTime.UtcNow;
                await db.OutboxMessages
                    .IgnoreQueryFilters()
                    .Where(m => m.Id == rowId && m.Status == OutboxStatus.Sending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.Status, OutboxStatus.Skipped)
                        .SetProperty(m => m.SkipReason, Truncate(verdict.Reason ?? "gate returned false", 500))
                        .SetProperty(m => m.GateVersion, verdict.GateVersion)
                        .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                        .SetProperty(m => m.LastError, (string?)null)
                        .SetProperty(m => m.UpdatedBy, "outbox-dispatcher")
                        .SetProperty(m => m.UpdatedOn, skippedAt), ct);
                _logger.LogInformation("[Outbox] Skipped {Tx} {Entity}:{Id} at dispatch re-check (gate v{GateVersion}): {Reason}",
                    row.TransactionType, row.EntityName, row.EntityId, verdict.GateVersion, verdict.Reason);
                return;
            }
        }

        // --- 2. POST (claim already won; the row is Sending and will not be re-POSTed by a sibling). --------------
        // R9 (D-R9-2): Dynamic route → config-driven JSONata pipeline; Legacy route or no config row → the
        // compiled path below, byte-identical to pre-R9 behaviour.
        var infor = sp.GetRequiredService<IInforIntegrationService>();
        var idem = sp.GetRequiredService<IOutboundIdempotencyContext>();

        InforSyncResult result;
        var permanentFailure = false;
        try
        {
            // Replay the SAME deterministic key (D2 fix) as the ERP idempotency key.
            idem.Set(row.DeterministicKey);
            if (route?.Mode == LnDispatchMode.Dynamic)
            {
                var dynamicOutcome = await sp.GetRequiredService<ILnDynamicDispatcher>().DispatchAsync(row, route, ct);
                result = dynamicOutcome.Result;
                permanentFailure = dynamicOutcome.PermanentFailure;
            }
            else
            {
                result = await InvokeAsync(infor, row, ct);
            }
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
            // FIX #2 (sync-ack seam) — if the ERP returned the entity's code INLINE in the POST response, the post is
            // already fully acknowledged: flip the row straight to the terminal Acked (stamp AckedAt) so it never sits
            // Dispatched-awaiting-an-async-ack (and the reconciliation sweep never alerts on it). When no inline code
            // is returned (Mock; or an LN BOD that acks asynchronously) the row stays Dispatched and the async
            // /inbound/erp-ack callback (or, failing that, SweepStaleDispatchedAsync) takes over.
            //
            // TODO (sync-ack write-back): the inline code currently flips the outbox row to Acked; writing the code
            // back onto the business entity (Supplier→SupCode, Asn→ASNNo, Invoice/Payment/…) reuses the per-entity
            // mapping that lives in UpsertErpAckCommand.StampErpCodeAsync (Application layer). Until that stamp helper
            // is lifted to a shared service the dispatcher can call, the async erp-ack remains the write-back path for
            // the entity code even when the row is acked inline here. The Mock returns ErpCode=null, so this branch is
            // dormant by default — no behaviour change for the existing Mock-mode tests.
            var hasInlineErpCode = !string.IsNullOrWhiteSpace(result.ErpCode);

            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == rowId && m.Status == OutboxStatus.Sending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, hasInlineErpCode ? OutboxStatus.Acked : OutboxStatus.Dispatched)
                    .SetProperty(m => m.AckedAt, hasInlineErpCode ? now : (DateTime?)null)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.ErrorClass, (string?)null)
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
                // R4 (2026-06-23) — persist the canonical "what we sent" body (built by the service, Mock + Live) so
                // the SyncLog payload viewer can render it. Capped to guard the SQL-Express size budget.
                PayloadJson = result.RequestPayloadJson is null ? null : Truncate(result.RequestPayloadJson, 1_000_000),
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

            // R9 (D-R9-5) — permanent dynamic-path failures (4xx / config bugs) are stamped with a stable
            // prefix (LastError) + marker (IntegrationError.StackTrace) so the errors UI can badge them and
            // warn on re-arm. Classification is code-owned; no schema change in Phase A.
            var lastError = permanentFailure
                ? LnRetriabilityClassifier.PermanentLastErrorPrefix + detail
                : detail;

            // Roll the claimed row Sending → Failed (POST did not land). The atomic claim already incremented
            // AttemptCount, so the failed row carries its attempt number. A future RetryIntegrationErrorCommand
            // re-arms it (Failed → Pending) — or, for an invoice, a GRN re-approval (ErpPostedAt still null) re-posts.
            await db.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.Id == rowId && m.Status == OutboxStatus.Sending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Failed)
                    .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                    .SetProperty(m => m.LastError, Truncate(lastError, 2000))
                    // R9 (D-R9-5, 0048) — persist the code-owned class for the monitor badge + re-arm warning.
                    .SetProperty(m => m.ErrorClass, permanentFailure ? "Permanent" : "Retriable")
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
                // R4 (2026-06-23) — keep the attempted payload on failures too, so a rejected ASN is still inspectable.
                PayloadJson = result.RequestPayloadJson is null ? null : Truncate(result.RequestPayloadJson, 1_000_000),
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
                // R9 (D-R9-5) — stable marker for permanent (4xx / config) dynamic-path failures; the errors
                // UI badges these and warns on re-arm (StackTrace doubles as the reason-marker column, same
                // precedent as DispatchedReconcileReason above).
                StackTrace = permanentFailure ? LnRetriabilityClassifier.PermanentErrorMarker : null,
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
            OutboxTransactionType.PoNegotiationApprove => await infor.ApprovePoNegotiationAsync(id, ct),
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
