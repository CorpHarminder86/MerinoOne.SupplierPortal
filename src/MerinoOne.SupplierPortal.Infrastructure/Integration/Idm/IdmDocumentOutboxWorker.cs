using System.Text.Json;
using System.Text.Json.Nodes;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §5. The IDM document-sync dispatcher. Mirrors the OutboxDispatcherWorker structure but
/// with the R8 semantics: a scan SEEDS Create/Delete rows keyed by <c>idmEntityType</c>, rows insert <c>Blocked</c>
/// and promote to <c>Pending</c> when the eligibility gate is satisfied (poll-based, self-healing), dispatch is
/// per-<c>documentUploadId</c> FIFO (strict order within a partition, parallel across partitions with a concurrency
/// cap), and soft-deleted portal documents emit an IDM delete then reap. The per-drain core is <c>internal static</c>
/// so tests drive it directly without the hosted loop.
/// </summary>
internal sealed class IdmDocumentOutboxWorker : BackgroundService
{
    // List (not array) so EF translates .Contains to a SQL IN — an array binds to the .NET 10 span-based
    // MemoryExtensions.Contains, which the EF funcletizer cannot evaluate.
    private static readonly List<IdmOutboxStatus> NonTerminal =
        new() { IdmOutboxStatus.Blocked, IdmOutboxStatus.Pending, IdmOutboxStatus.InFlight };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInforIdmSettings _settings;
    private readonly ILogger<IdmDocumentOutboxWorker> _logger;

    public IdmDocumentOutboxWorker(IServiceScopeFactory scopeFactory, IInforIdmSettings settings, ILogger<IdmDocumentOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IdmDocumentOutboxWorker started. Poll={Poll}s Batch={Batch} Concurrency={Cap}",
            _settings.DispatcherPollSeconds, _settings.BatchSize, _settings.ConcurrencyCap);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(_scopeFactory, _settings, _logger, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "IdmDocumentOutboxWorker drain failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.DispatcherPollSeconds)), stoppingToken); }
            catch (TaskCanceledException) { /* shutdown */ }
        }

        _logger.LogInformation("IdmDocumentOutboxWorker stopped.");
    }

    /// <summary>The testable per-drain core: maintenance (stale sweep + seed + promote + unresolvable) → dispatch → reap.</summary>
    internal static async Task DrainOnceAsync(IServiceScopeFactory scopeFactory, IInforIdmSettings settings, ILogger logger, CancellationToken ct)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var registry = scope.ServiceProvider.GetRequiredService<ISnapshotProviderRegistry>();
            var gate = scope.ServiceProvider.GetRequiredService<IEligibilityGate>();

            await SweepStaleInFlightAsync(db, settings.StaleInFlightMinutes, logger, ct);
            await SeedAndPromoteAsync(db, registry, gate, settings.BatchSize, logger, ct);
        }

        List<Guid> heads;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            heads = await SelectDueHeadRowsAsync(db, settings.BatchSize, DateTime.UtcNow, ct);
        }

        if (heads.Count > 0)
        {
            using var sem = new SemaphoreSlim(Math.Clamp(settings.ConcurrencyCap, 1, 16));
            var tasks = heads.Select(async id =>
            {
                await sem.WaitAsync(ct);
                try { await DispatchRowAsync(scopeFactory, id, settings, logger, ct); }
                catch (Exception ex) { logger.LogError(ex, "[IDM] dispatch of {Row} failed.", id); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        }

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            await ReapAsync(db, logger, ct);
        }
    }

    // ── Stale-InFlight recovery: a crash after the claim commits (row InFlight) but before write-back. ──────────
    private static async Task SweepStaleInFlightAsync(IAppDbContext db, int staleMinutes, ILogger logger, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, staleMinutes));
        var now = DateTime.UtcNow;
        var reset = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Status == IdmOutboxStatus.InFlight
                        && (o.UpdatedOn ?? o.CreatedOn) < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, IdmOutboxStatus.Pending)
                .SetProperty(o => o.LastError, "Re-armed by stale-InFlight sweep (crash-mid-dispatch recovery).")
                .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                .SetProperty(o => o.UpdatedOn, now), ct);
        if (reset > 0) logger.LogWarning("[IDM] Stale-InFlight sweep re-armed {Count} row(s) to Pending.", reset);
    }

    // ── Seeding + gate promotion + unresolvable, per enabled config. ────────────────────────────────────────────
    internal static async Task SeedAndPromoteAsync(IAppDbContext db, ISnapshotProviderRegistry registry,
        IEligibilityGate gate, int batchSize, ILogger logger, CancellationToken ct)
    {
        var configs = await db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.IsEnabled && !c.IsDeleted)
            .ToListAsync(ct);

        foreach (var cfg in configs)
        {
            var provider = registry.TryGet(cfg.IdmEntityType);
            if (provider is null || cfg.TenantId is not { } tenantId) continue;

            // Portal entity: the stored value wins; fall back to the provider for pre-2026-07-06 rows.
            var ownerType = string.IsNullOrEmpty(cfg.OwnerEntityType) ? provider.OwnerEntityType : cfg.OwnerEntityType;
            // Optional attachment-type filter: NULL config = catch-all (every document of this portal entity).
            var attachmentType = cfg.AttachmentType;

            // Fix: stamp idmEntityType on matching not-yet-classified uploads so the steady-state upload/replace
            // flow seeds without a manual backfill (verifier BLOCKER). Build the attachment-type filter in C#
            // (NOT `attachmentType == null || …` in the predicate — EF translates a null parameter inconsistently
            // between ExecuteUpdate and a projected query, which silently dropped catch-all matches).
            var stampQuery = db.DocumentUploads.IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && d.TenantId == tenantId && d.IdmEntityType == null && d.OwnerEntityType == ownerType);
            if (attachmentType != null) stampQuery = stampQuery.Where(d => d.DocumentType == attachmentType);
            await stampQuery
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IdmEntityType, cfg.IdmEntityType)
                    .SetProperty(d => d.UpdatedBy, "idm-dispatcher")
                    .SetProperty(d => d.UpdatedOn, DateTime.UtcNow), ct);

            await SeedCreatesAsync(db, provider, gate, cfg, ownerType, attachmentType, tenantId, batchSize, ct);
            await SeedDeletesAsync(db, cfg, ownerType, attachmentType, tenantId, batchSize, ct);
            await PromoteBlockedAsync(db, provider, gate, cfg, tenantId, batchSize, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedCreatesAsync(IAppDbContext db, IEntitySnapshotProvider provider, IEligibilityGate gate,
        IdmAttachmentTypeConfig cfg, string ownerType, string? attachmentType, Guid tenantId, int batchSize, CancellationToken ct)
    {
        // Match on (ownerEntityType, documentType) — the portal entity plus, when set, the attachment type
        // (NULL attachmentType = catch-all: every document of this entity). NOT idmEntityType (D7: many types
        // may share one entityType, which would make every such config seed the same document). Attachment filter
        // built in C# (see the stamp query above for why an inline `== null` OR is unsafe).
        var candQuery = db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .Where(d => !d.IsDeleted && d.TenantId == tenantId && d.Pid == null && d.OwnerEntityType == ownerType);
        if (attachmentType != null) candQuery = candQuery.Where(d => d.DocumentType == attachmentType);
        var candidates = await candQuery
            .Select(d => new { d.Id, d.OwnerEntityId, d.SeccodeId, d.TenantId, d.TenantEntityId, d.FileName })
            .Take(batchSize)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var docIds = candidates.Select(c => c.Id).ToList();
        // Dedupe against ANY non-reaped Create row (incl. terminal Failed/Success) — a terminal 4xx must NOT re-seed
        // every poll (verifier fix; D-R8-23). Retry re-arms instead.
        var existing = (await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Operation == IdmOutboxOperation.Create && docIds.Contains(o.DocumentUploadId))
            .Select(o => o.DocumentUploadId).ToListAsync(ct)).ToHashSet();

        foreach (var d in candidates)
        {
            if (existing.Contains(d.Id)) continue;
            var snapshot = await provider.BuildSnapshotAsync(tenantId, d.OwnerEntityId, d.Id, includeFileContent: false, ct);
            var eligible = snapshot is not null && gate.IsSatisfied(cfg.EligibilityGateExpr, snapshot);
            db.IdmDocumentOutboxes.Add(new IdmDocumentOutbox
            {
                DocumentUploadId = d.Id,
                IdmEntityType = cfg.IdmEntityType,
                OwnerEntityId = d.OwnerEntityId,
                FileName = d.FileName,
                Operation = IdmOutboxOperation.Create,
                Status = eligible ? IdmOutboxStatus.Pending : IdmOutboxStatus.Blocked,
                SeccodeId = d.SeccodeId,
                TenantId = d.TenantId,
                TenantEntityId = d.TenantEntityId,
                CreatedBy = "idm-dispatcher",
            });
        }
    }

    private static async Task SeedDeletesAsync(IAppDbContext db, IdmAttachmentTypeConfig cfg, string ownerType, string? attachmentType, Guid tenantId, int batchSize, CancellationToken ct)
    {
        // Soft-deleted documents that WERE synced (pid present) emit an IDM delete. Match on (ownerEntityType,
        // documentType) so a document is handled by exactly one config (owner filter added 2026-07-06 — a catch-all
        // NULL attachmentType would otherwise match every entity's deleted docs).
        var deletedQuery = db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsDeleted && d.Pid != null && d.TenantId == tenantId && d.OwnerEntityType == ownerType);
        if (attachmentType != null) deletedQuery = deletedQuery.Where(d => d.DocumentType == attachmentType);
        var deleted = await deletedQuery
            .Select(d => new { d.Id, d.OwnerEntityId, d.SeccodeId, d.TenantId, d.TenantEntityId, d.FileName, d.Pid })
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var d in deleted)
        {
            var hasDelete = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .AnyAsync(o => !o.IsDeleted && o.Operation == IdmOutboxOperation.Delete && o.DocumentUploadId == d.Id, ct);
            if (hasDelete) continue;
            db.IdmDocumentOutboxes.Add(new IdmDocumentOutbox
            {
                DocumentUploadId = d.Id,
                IdmEntityType = cfg.IdmEntityType,
                OwnerEntityId = d.OwnerEntityId,
                FileName = d.FileName,
                Operation = IdmOutboxOperation.Delete,
                Status = IdmOutboxStatus.Pending,
                ExternalId = d.Pid,
                SeccodeId = d.SeccodeId,
                TenantId = d.TenantId,
                TenantEntityId = d.TenantEntityId,
                CreatedBy = "idm-dispatcher",
            });
        }

        // Soft-deleted documents that were NEVER synced (no pid): reap any non-terminal Create rows immediately (D-R8-6).
        var neverSyncedQuery = db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsDeleted && d.Pid == null && d.TenantId == tenantId && d.OwnerEntityType == ownerType);
        if (attachmentType != null) neverSyncedQuery = neverSyncedQuery.Where(d => d.DocumentType == attachmentType);
        var neverSynced = await neverSyncedQuery
            .Select(d => d.Id)
            .Take(batchSize)
            .ToListAsync(ct);
        if (neverSynced.Count > 0)
        {
            var now = DateTime.UtcNow;
            await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => !o.IsDeleted && neverSynced.Contains(o.DocumentUploadId) && NonTerminal.Contains(o.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.IsDeleted, true)
                    .SetProperty(o => o.DeletedOn, now)
                    .SetProperty(o => o.DeletedBy, "idm-dispatcher"), ct);
        }
    }

    private static async Task PromoteBlockedAsync(IAppDbContext db, IEntitySnapshotProvider provider, IEligibilityGate gate,
        IdmAttachmentTypeConfig cfg, Guid tenantId, int batchSize, CancellationToken ct)
    {
        var blocked = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Status == IdmOutboxStatus.Blocked
                        && o.TenantId == tenantId && o.IdmEntityType == cfg.IdmEntityType)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var row in blocked)
        {
            var snapshot = await provider.BuildSnapshotAsync(tenantId, row.OwnerEntityId, row.DocumentUploadId, includeFileContent: false, ct);
            if (snapshot is null)
            {
                // Owner/document no longer resolves — terminal, keep the log actionable (D-R8-7 conservative substitute).
                row.Status = IdmOutboxStatus.Unresolvable;
                row.LastError = "Owning entity or document no longer resolves.";
                row.UpdatedBy = "idm-dispatcher";
                row.UpdatedOn = DateTime.UtcNow;
                continue;
            }
            if (gate.IsSatisfied(cfg.EligibilityGateExpr, snapshot))
            {
                row.Status = IdmOutboxStatus.Pending;
                row.UpdatedBy = "idm-dispatcher";
                row.UpdatedOn = DateTime.UtcNow;
            }
        }
    }

    // ── Per-partition FIFO head selection: only the lowest-Seq non-terminal row of each partition, when it is a due Pending. ──
    internal static async Task<List<Guid>> SelectDueHeadRowsAsync(IAppDbContext db, int batchSize, DateTime now, CancellationToken ct)
    {
        var partitions = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Status == IdmOutboxStatus.Pending && (o.NextAttemptAt == null || o.NextAttemptAt <= now))
            .Select(o => o.DocumentUploadId)
            .Distinct()
            .Take(batchSize)
            .ToListAsync(ct);

        var heads = new List<Guid>(partitions.Count);
        foreach (var partition in partitions)
        {
            var head = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => !o.IsDeleted && o.DocumentUploadId == partition && NonTerminal.Contains(o.Status))
                .OrderBy(o => o.Seq)
                .Select(o => new { o.Id, o.Status, o.NextAttemptAt })
                .FirstOrDefaultAsync(ct);
            if (head is null || head.Status != IdmOutboxStatus.Pending) continue;              // hold successors behind an InFlight/Blocked head
            if (head.NextAttemptAt is { } next && next > now) continue;                          // backoff not elapsed
            heads.Add(head.Id);
        }
        return heads;
    }

    // ── Dispatch one row (own scope for thread-safety under the concurrency cap). ────────────────────────────────
    private static async Task DispatchRowAsync(IServiceScopeFactory scopeFactory, Guid rowId, IInforIdmSettings settings, ILogger logger, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ISnapshotProviderRegistry>();
        var gate = scope.ServiceProvider.GetRequiredService<IEligibilityGate>();
        var builder = scope.ServiceProvider.GetRequiredService<IOutboundRequestBuilder>();
        var client = scope.ServiceProvider.GetRequiredService<IIdmClient>();
        var ackParser = scope.ServiceProvider.GetRequiredService<IIdmAckParser>();

        // Read + atomic claim (Pending → InFlight) gated by RowVersion — exactly-once under parallelism/restart.
        var snap = await db.IdmDocumentOutboxes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == rowId, ct);
        if (snap is null || snap.IsDeleted || snap.Status != IdmOutboxStatus.Pending) return;

        var claimVersion = snap.RowVersion;
        var claimedAt = DateTime.UtcNow;
        var claimed = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId && o.Status == IdmOutboxStatus.Pending && !o.IsDeleted && o.RowVersion == claimVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, IdmOutboxStatus.InFlight)
                .SetProperty(o => o.AttemptCount, o => o.AttemptCount + 1)
                .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                .SetProperty(o => o.UpdatedOn, claimedAt), ct);
        if (claimed != 1) return; // lost the claim

        var row = await db.IdmDocumentOutboxes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == rowId, ct);
        if (row is null) return;

        var cfg = await db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == row.TenantId && c.IdmEntityType == row.IdmEntityType && !c.IsDeleted, ct);
        var provider = registry.TryGet(row.IdmEntityType);
        var tenantId = row.TenantId ?? Guid.Empty;
        var endpointKey = $"IDM.Item.{row.Operation}";

        OutboundEnvelope envelope;
        string persistSnapshotJson;

        if (row.Operation == IdmOutboxOperation.Delete)
        {
            // Uniform delete shape — pid only (no file fetch).
            envelope = new OutboundEnvelope(
                new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                JsonSerializer.Serialize(new { item = new { pid = row.ExternalId ?? string.Empty } }));
            persistSnapshotJson = JsonSerializer.Serialize(new { operation = "Delete", pid = row.ExternalId });
        }
        else
        {
            if (cfg is null || provider is null)
            {
                await FailAsync(db, rowId, "Missing IDM type config / snapshot provider.", ct);
                return;
            }
            var snapshot = await provider.BuildSnapshotAsync(tenantId, row.OwnerEntityId, row.DocumentUploadId, includeFileContent: true, ct);
            if (snapshot is null)
            {
                await FailAsync(db, rowId, "Owning entity or document no longer resolves.", ct);
                return;
            }
            // Create re-check: if the gate fell unsatisfied, drop back to Blocked (do not send an incomplete key).
            if (row.Operation == IdmOutboxOperation.Create && !gate.IsSatisfied(cfg.EligibilityGateExpr, snapshot))
            {
                await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                    .Where(o => o.Id == rowId && o.Status == IdmOutboxStatus.InFlight)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, IdmOutboxStatus.Blocked)
                        .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                        .SetProperty(o => o.UpdatedOn, DateTime.UtcNow), ct);
                return;
            }

            var expression = row.Operation == IdmOutboxOperation.Update
                ? (cfg.MutateMappingExpression ?? cfg.CreateMappingExpression)
                : cfg.CreateMappingExpression;
            envelope = await builder.BuildAsync(expression, snapshot, ct);
            envelope = await MergeStaticHeaders(db, envelope, tenantId, endpointKey, ct);
            persistSnapshotJson = BuildPersistSnapshot(envelope.Headers, snapshot);
        }

        // Persist the elided request snapshot before sending (never store base64 — D-R8-18).
        await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RequestSnapshotJson, Truncate(persistSnapshotJson, 1_000_000)), ct);

        IdmHttpResult result;
        try { result = await client.SendAsync(tenantId, endpointKey, envelope, ct); }
        catch (Exception ex) { result = new IdmHttpResult(0, ex.Message, true); }

        var now = DateTime.UtcNow;

        // Transport failure → transient.
        if (result.TransportFailure)
        {
            await ApplyTransientAsync(db, rowId, row.AttemptCount, settings, result.Body, now, ct);
            return;
        }

        var ack = ackParser.Parse(result.StatusCode, result.Body);

        // Delete is special: the IDM delete response carries NO <pid>, so the pid-requiring parser would wrongly
        // flag a real success as Validation. Success = any 2xx; a gone pid (404 / "does not exist") is a no-op
        // Success (D-R8-5); 5xx = transient; other 4xx = terminal Failed.
        if (row.Operation == IdmOutboxOperation.Delete)
        {
            var okOrGone = (result.StatusCode is >= 200 and < 300)
                || result.StatusCode == 404
                || (ack.Detail?.Contains("not exist", StringComparison.OrdinalIgnoreCase) ?? false);
            if (okOrGone)
            {
                await SucceedAsync(db, rowId, row, null, result.Body, now, ct);
                logger.LogInformation("[IDM] Delete doc={Doc} → Success.", row.DocumentUploadId);
            }
            else if (result.StatusCode >= 500)
            {
                await ApplyTransientAsync(db, rowId, row.AttemptCount, settings, ack.Detail, now, ct);
            }
            else
            {
                await FailAsync(db, rowId, ack.Detail ?? "IDM delete failed.", ct, result.Body);
            }
            return;
        }

        switch (ack.Failure)
        {
            case IdmFailureClass.None:
                await SucceedAsync(db, rowId, row, ack, result.Body, now, ct);
                logger.LogInformation("[IDM] {Op} doc={Doc} → Success pid={Pid}.", row.Operation, row.DocumentUploadId, ack.Pid);
                break;
            case IdmFailureClass.Transient:
                await ApplyTransientAsync(db, rowId, row.AttemptCount, settings, ack.Detail, now, ct);
                break;
            default: // Validation → terminal Failed, no retry (D-R8-23)
                await FailAsync(db, rowId, ack.Detail ?? "IDM validation failure.", ct, result.Body);
                logger.LogWarning("[IDM] {Op} doc={Doc} → Failed (validation): {Detail}", row.Operation, row.DocumentUploadId, ack.Detail);
                break;
        }
    }

    private static async Task<OutboundEnvelope> MergeStaticHeaders(IAppDbContext db, OutboundEnvelope envelope, Guid tenantId, string endpointKey, CancellationToken ct)
    {
        var staticJson = await db.OutboundEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EndpointKey == endpointKey && !e.IsDeleted)
            .Select(e => e.StaticHeadersJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(staticJson)) return envelope;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var node = JsonNode.Parse(staticJson);
            if (node is JsonObject obj)
                foreach (var (k, v) in obj)
                    if (v is not null) merged[k] = v.ToString();
        }
        catch (JsonException) { /* malformed static headers → ignore, expression headers stand */ }

        foreach (var (k, v) in envelope.Headers) merged[k] = v; // expression headers win
        return envelope with { Headers = merged };
    }

    private static string BuildPersistSnapshot(IReadOnlyDictionary<string, string> headers, object snapshot)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(snapshot));
        if (node?["attachment"]?["base64"] is JsonNode b64)
        {
            var len = b64.ToString().Length;
            node!["attachment"]!["base64"] = $"<elided {len} chars>";
        }
        return JsonSerializer.Serialize(new { headers, snapshot = node });
    }

    private static async Task SucceedAsync(IAppDbContext db, Guid rowId, IdmDocumentOutbox row, IdmAck? ack, string responseBody, DateTime now, CancellationToken ct)
    {
        var pid = ack?.Pid ?? row.ExternalId;
        await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId && o.Status == IdmOutboxStatus.InFlight)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, IdmOutboxStatus.Success)
                .SetProperty(o => o.ExternalId, pid)
                .SetProperty(o => o.ResponseJson, Truncate(responseBody, 1_000_000))
                .SetProperty(o => o.LastError, (string?)null)
                .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                .SetProperty(o => o.UpdatedOn, now), ct);

        // On a first successful Create, stamp the pid onto the document so mutations reuse it (D-R8-24).
        if (row.Operation == IdmOutboxOperation.Create && !string.IsNullOrEmpty(pid))
            await db.DocumentUploads.IgnoreQueryFilters()
                .Where(d => d.Id == row.DocumentUploadId && d.Pid == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Pid, pid)
                    .SetProperty(d => d.UpdatedBy, "idm-dispatcher")
                    .SetProperty(d => d.UpdatedOn, now), ct);

        // On a successful Delete, clear the document's pid — the IDM copy is gone, so the delete-seed predicate
        // (IsDeleted && Pid != null) no longer matches and the row is not re-seeded after it reaps.
        if (row.Operation == IdmOutboxOperation.Delete)
            await db.DocumentUploads.IgnoreQueryFilters()
                .Where(d => d.Id == row.DocumentUploadId && d.Pid != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Pid, (string?)null)
                    .SetProperty(d => d.UpdatedBy, "idm-dispatcher")
                    .SetProperty(d => d.UpdatedOn, now), ct);
    }

    private static async Task ApplyTransientAsync(IAppDbContext db, Guid rowId, int attemptCount, IInforIdmSettings settings, string? detail, DateTime now, CancellationToken ct)
    {
        if (IdmBackoffPolicy.IsExhausted(attemptCount, settings.MaxAttempts))
        {
            await FailAsync(db, rowId, $"Exhausted {settings.MaxAttempts} attempts. Last: {detail}", ct);
            return;
        }
        var delay = IdmBackoffPolicy.NextDelay(attemptCount, settings.RetryBackoffBaseSeconds, settings.RetryBackoffCapSeconds);
        await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId && o.Status == IdmOutboxStatus.InFlight)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, IdmOutboxStatus.Pending)
                .SetProperty(o => o.NextAttemptAt, now.Add(delay))
                .SetProperty(o => o.LastError, Truncate(detail ?? "Transient IDM failure.", 4000))
                .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                .SetProperty(o => o.UpdatedOn, now), ct);
    }

    private static async Task FailAsync(IAppDbContext db, Guid rowId, string detail, CancellationToken ct, string? responseBody = null)
    {
        var now = DateTime.UtcNow;
        await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId && (o.Status == IdmOutboxStatus.InFlight || o.Status == IdmOutboxStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, IdmOutboxStatus.Failed)
                .SetProperty(o => o.LastError, Truncate(detail, 4000))
                .SetProperty(o => o.ResponseJson, responseBody == null ? null : Truncate(responseBody, 1_000_000))
                .SetProperty(o => o.UpdatedBy, "idm-dispatcher")
                .SetProperty(o => o.UpdatedOn, now), ct);
    }

    // ── Reap: a successful Delete row (and its terminal siblings for the same document) becomes reap-eligible. ──
    private static async Task ReapAsync(IAppDbContext db, ILogger logger, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var doneDeletes = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Operation == IdmOutboxOperation.Delete && o.Status == IdmOutboxStatus.Success)
            .Select(o => o.DocumentUploadId)
            .Take(200)
            .ToListAsync(ct);
        if (doneDeletes.Count == 0) return;

        var reaped = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && doneDeletes.Contains(o.DocumentUploadId)
                        && (o.Status == IdmOutboxStatus.Success || o.Status == IdmOutboxStatus.Failed || o.Status == IdmOutboxStatus.Unresolvable))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.IsDeleted, true)
                .SetProperty(o => o.DeletedOn, now)
                .SetProperty(o => o.DeletedBy, "idm-dispatcher"), ct);
        if (reaped > 0) logger.LogInformation("[IDM] Reaped {Count} terminal outbox row(s) after delete-ack.", reaped);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
