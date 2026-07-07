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

    // ── Seeding + gate promotion + unresolvable, per active config. ─────────────────────────────────────────────
    // R10 — configs live on the unified integration.OutboundIntegrationConfig (Kind=Document). Dynamic = active;
    // Held = fully off for seeding AND dispatch (exact parity with the old IsEnabled=false — a held Document
    // integration must not silently accumulate outbox rows the way LN's per-endpoint Held does).
    internal static async Task SeedAndPromoteAsync(IAppDbContext db, ISnapshotProviderRegistry registry,
        IEligibilityGate gate, int batchSize, ILogger logger, CancellationToken ct)
    {
        var configs = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Kind == OutboundIntegrationKind.Document
                        && c.DispatchMode == OutboundDispatchMode.Dynamic
                        && c.TargetEntityName != null && !c.IsDeleted)
            .ToListAsync(ct);

        foreach (var cfg in configs)
        {
            var provider = registry.TryGet(cfg.TargetEntityName!);
            if (provider is null || cfg.TenantId is not { } tenantId) continue;

            var ownerType = cfg.PortalEntity;
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
                    .SetProperty(d => d.IdmEntityType, cfg.TargetEntityName)
                    .SetProperty(d => d.UpdatedBy, "idm-dispatcher")
                    .SetProperty(d => d.UpdatedOn, DateTime.UtcNow), ct);

            await SeedCreatesAsync(db, provider, gate, cfg, ownerType, attachmentType, tenantId, batchSize, ct);
            await SeedDeletesAsync(db, cfg, ownerType, attachmentType, tenantId, batchSize, ct);
            await PromoteBlockedAsync(db, provider, gate, cfg, tenantId, batchSize, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>R10 gate rule, aligned with the LN plane: a blank/NULL gate = no gate = eligible (was: the R8
    /// dot-path column was required). Non-blank gates keep the strict-true fail-closed engine semantics.</summary>
    private static bool GatePasses(IEligibilityGate gate, string? gateExpr, object snapshot)
        => string.IsNullOrWhiteSpace(gateExpr) || gate.IsSatisfied(gateExpr, snapshot);

    private static async Task SeedCreatesAsync(IAppDbContext db, IEntitySnapshotProvider provider, IEligibilityGate gate,
        OutboundIntegrationConfig cfg, string ownerType, string? attachmentType, Guid tenantId, int batchSize, CancellationToken ct)
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
            var eligible = snapshot is not null && GatePasses(gate, cfg.EligibilityGateExpr, snapshot);
            db.IdmDocumentOutboxes.Add(new IdmDocumentOutbox
            {
                DocumentUploadId = d.Id,
                IdmEntityType = cfg.TargetEntityName!,
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

    private static async Task SeedDeletesAsync(IAppDbContext db, OutboundIntegrationConfig cfg, string ownerType, string? attachmentType, Guid tenantId, int batchSize, CancellationToken ct)
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
                IdmEntityType = cfg.TargetEntityName!,
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
        OutboundIntegrationConfig cfg, Guid tenantId, int batchSize, CancellationToken ct)
    {
        var blocked = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && o.Status == IdmOutboxStatus.Blocked
                        && o.TenantId == tenantId && o.IdmEntityType == cfg.TargetEntityName)
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
            if (GatePasses(gate, cfg.EligibilityGateExpr, snapshot))
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
        var mapping = scope.ServiceProvider.GetRequiredService<Application.Integration.Ln.ILnMappingService>();

        // Read + atomic claim (Pending → InFlight) gated by RowVersion — exactly-once under parallelism/restart.
        var snap = await db.IdmDocumentOutboxes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(o => o.Id == rowId, ct);
        if (snap is null || snap.IsDeleted || snap.Status != IdmOutboxStatus.Pending) return;

        // R10 — resolve THE config the same way the seeding scan matched the document: by the document's
        // (ownerEntityType, documentType), a specific-attachment-type row beating the catch-all (D7). NOT by
        // targetEntityName — many rows may share one entity type, which made the lookup ambiguous.
        var docKey = await db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Id == snap.DocumentUploadId)
            .Select(d => new { d.OwnerEntityType, d.DocumentType })
            .FirstOrDefaultAsync(ct);
        OutboundIntegrationConfig? cfg = null;
        if (docKey is not null)
        {
            var candidates = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == snap.TenantId && c.Kind == OutboundIntegrationKind.Document
                            && c.PortalEntity == docKey.OwnerEntityType && !c.IsDeleted
                            && (c.AttachmentType == null || c.AttachmentType == docKey.DocumentType))
                .ToListAsync(ct);
            cfg = candidates.OrderBy(c => c.AttachmentType == null ? 1 : 0).ThenBy(c => c.Seq).FirstOrDefault();
        }

        // R10 — Held pre-claim gate: a held integration stops dispatch of already-Pending rows too (kill
        // stops dispatch; the seeding scan is separately fenced to Dynamic).
        if (cfg?.DispatchMode == OutboundDispatchMode.Held) return;

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

        var provider = registry.TryGet(row.IdmEntityType);
        var tenantId = row.TenantId ?? Guid.Empty;

        if (cfg is null)
        {
            // R10 — routing (verb/path) lives on the config row for EVERY operation now, so a deleted config
            // stops Deletes too (pre-R10 they could still resolve the separate transport row).
            await FailAsync(db, rowId, "Missing outbound integration config (Document kind) for this entity type.", ct);
            return;
        }

        // Per-operation routing from the unified row (NULL mutate/delete path = reuse the create path).
        var (verb, path) = row.Operation switch
        {
            IdmOutboxOperation.Update => (cfg.MutateVerb ?? cfg.HttpVerb, cfg.MutatePath ?? cfg.EndpointPath),
            IdmOutboxOperation.Delete => (cfg.DeleteVerb ?? "DELETE", cfg.DeletePath ?? cfg.EndpointPath),
            _ => (cfg.HttpVerb, cfg.EndpointPath),
        };

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
            if (provider is null)
            {
                await FailAsync(db, rowId, "No snapshot provider for this entity type.", ct);
                return;
            }
            var snapshot = await provider.BuildSnapshotAsync(tenantId, row.OwnerEntityId, row.DocumentUploadId, includeFileContent: true, ct);
            if (snapshot is null)
            {
                await FailAsync(db, rowId, "Owning entity or document no longer resolves.", ct);
                return;
            }
            // Missing file bytes = Validation-class TERMINAL failure (the D-R8-18 contract) — posting a
            // content-less item just makes the remote reject it (observed: Live IDM 500 → a pointless
            // transient-retry loop until attempts exhaust). Fail fast with an actionable message instead.
            if (!SnapshotHasFileContent(snapshot))
            {
                await FailAsync(db, rowId,
                    $"File content missing from storage — re-upload the document (file: {row.FileName}).", ct);
                logger.LogWarning("[IDM] {Op} doc={Doc} → Failed: file bytes missing from storage.", row.Operation, row.DocumentUploadId);
                return;
            }
            // Create re-check: if the gate fell unsatisfied, drop back to Blocked (do not send an incomplete key).
            if (row.Operation == IdmOutboxOperation.Create && !GatePasses(gate, cfg.EligibilityGateExpr, snapshot))
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
                ? (cfg.MutateMappingExpr ?? cfg.RequestMappingExpr)
                : cfg.RequestMappingExpr;
            envelope = await builder.BuildAsync(expression, snapshot, ct);
            envelope = MergeStaticHeaders(envelope, cfg.StaticHeadersJson);
            persistSnapshotJson = BuildPersistSnapshot(envelope.Headers, envelope.Body, snapshot);
        }

        // Persist the elided request snapshot before sending (never store base64 — D-R8-18).
        await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.Id == rowId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RequestSnapshotJson, Truncate(persistSnapshotJson, 1_000_000)), ct);

        IdmHttpResult result;
        try { result = await client.SendAsync(tenantId, row.Operation, verb, path, envelope, ct); }
        catch (Exception ex) { result = new IdmHttpResult(0, ex.Message, true); }

        var now = DateTime.UtcNow;

        // Transport failure → transient.
        if (result.TransportFailure)
        {
            await ApplyTransientAsync(db, rowId, row.AttemptCount, settings, result.Body, now, ct);
            return;
        }

        // R10 — response extraction: a configured response mapping (over the format-normalized body) replaces
        // the hardcoded parser for 2xx Create/Update; everything else (non-2xx classification, Delete, blank
        // expression) stays on the code-owned parser. Retriability NEVER flows from an expression (D-R9-5).
        var ack = row.Operation != IdmOutboxOperation.Delete
                  && result.StatusCode is >= 200 and < 300
                  && !string.IsNullOrWhiteSpace(cfg.ResponseMappingExpr)
            ? ExtractAckViaExpression(mapping, cfg.ResponseMappingExpr!, cfg.ResponseFormat, result.Body)
            : ackParser.Parse(result.StatusCode, result.Body);

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

    private static OutboundEnvelope MergeStaticHeaders(OutboundEnvelope envelope, string? staticJson)
    {
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

    /// <summary>
    /// R10 — expression-based success extraction. The 2xx body is normalized to JSON per the config's
    /// declared responseFormat (Xml → <see cref="XmlJsonNormalizer"/>), then the response mapping runs and
    /// must yield <c>{pid: "…"}</c> (or a bare pid string). A 2xx whose content cannot produce a pid is a
    /// Validation failure — same never-silent-success posture as the code parser (D-R8-22).
    /// </summary>
    internal static IdmAck ExtractAckViaExpression(Application.Integration.Ln.ILnMappingService mapping,
        string responseExpr, string responseFormat, string body)
    {
        var json = string.Equals(responseFormat, "Xml", StringComparison.OrdinalIgnoreCase)
            ? XmlJsonNormalizer.TryToJson(body)
            : body;
        if (string.IsNullOrWhiteSpace(json))
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation,
                $"2xx response body is not well-formed {responseFormat} — cannot extract pid.");

        var eval = mapping.Evaluate(responseExpr, json!);
        if (!eval.Ok || string.IsNullOrWhiteSpace(eval.OutputJson))
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation,
                $"Response mapping failed: {eval.Error ?? "no output"}.");

        try
        {
            var node = JsonNode.Parse(eval.OutputJson!);
            var pid = node switch
            {
                JsonObject obj when obj.TryGetPropertyValue("pid", out var p) => p?.GetValue<string>(),
                JsonValue val => val.GetValue<string>(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(pid)
                ? new IdmAck(null, null, null, null, IdmFailureClass.Validation, "Response mapping produced no pid.")
                : new IdmAck(pid, null, null, null, IdmFailureClass.None, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation,
                $"Response mapping output is not a pid contract: {ex.Message}");
        }
    }

    /// <summary>True when the dispatch snapshot carries the file's base64 (<c>attachment.base64</c> non-null).
    /// Providers return null there when <c>IFileContentProvider</c> cannot read the stored bytes.</summary>
    internal static bool SnapshotHasFileContent(object snapshot)
        => snapshot is IDictionary<string, object?> s
           && s.TryGetValue("attachment", out var a) && a is IDictionary<string, object?> att
           && att.TryGetValue("base64", out var b64) && b64 is string { Length: > 0 };

    /// <summary>
    /// R10 (2026-07-07) — the persisted detail now carries the RENDERED request body (what actually went on
    /// the wire) alongside the mapping-input snapshot, both with every base64 value elided (file bytes are
    /// never persisted — D-R8-18). Pre-R10 rows stored only {headers, snapshot}, which read as "the request"
    /// in the viewer and confused mapping debugging.
    /// </summary>
    private static string BuildPersistSnapshot(IReadOnlyDictionary<string, string> headers, string renderedBody, object snapshot)
    {
        var snapshotNode = JsonNode.Parse(JsonSerializer.Serialize(snapshot));
        ElideBase64(snapshotNode);
        JsonNode? bodyNode = null;
        try { bodyNode = JsonNode.Parse(renderedBody); ElideBase64(bodyNode); }
        catch (JsonException) { /* non-JSON body — persist the snapshot side only */ }
        return JsonSerializer.Serialize(new { headers, body = bodyNode, snapshot = snapshotNode });
    }

    /// <summary>Recursively replaces every property named <c>base64</c> holding a long string with an elision marker.</summary>
    private static void ElideBase64(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key.Equals("base64", StringComparison.OrdinalIgnoreCase)
                        && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 128)
                        obj[key] = $"<elided {s.Length} chars>";
                    else
                        ElideBase64(obj[key]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) ElideBase64(item);
                break;
        }
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
