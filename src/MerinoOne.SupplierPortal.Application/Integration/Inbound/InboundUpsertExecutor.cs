using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ForbiddenException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ForbiddenException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Shared orchestration for the inbound master-data upsert path (Payment Term / Delivery Term), per the
/// TenantCompany module §4. Both commands delegate the cross-cutting steps here and supply only the
/// entity-specific row upsert via <paramref name="upsertAsync"/>:
/// <list type="number">
///   <item>Resolve <c>CompanyCode → TenantEntityId</c> within the key's tenant. Unknown ⇒ ValidationException (400).</item>
///   <item>Normalize: <c>sourceId = ResolveSource(endpoint, incomingCompanyId)</c> (e.g. 3000 → 2000).</item>
///   <item>Anti-spoof: require <c>sourceId == key.BoundCompanyId</c> else ForbiddenException (403).</item>
///   <item>Endpoint gate: <c>InforEndpointMap(EntityName, Direction=Inbound)</c> missing/disabled ⇒ reject (kill-switch).</item>
///   <item>Idempotency: <c>Idempotency-Key</c> header else SHA-256 of the canonical payload; prior Success SyncLog ⇒ short-circuit.</item>
///   <item>Transactional per-row upsert via the supplied callback (keyed on (sourceId, Code), IgnoreQueryFilters).</item>
///   <item>One InforSyncLog (<c>payloadRef="recv:&lt;incoming&gt;;src:&lt;source&gt;;count:&lt;n&gt;"</c>); on failure a linked
///         IntegrationError; the InforEndpointMap session columns updated — all in the same transaction.</item>
/// </list>
/// </summary>
public class InboundUpsertExecutor
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ICurrentCompany _company;
    private readonly ILogger<InboundUpsertExecutor> _logger;

    public InboundUpsertExecutor(
        IAppDbContext db,
        ICurrentUser user,
        ICurrentCompany company,
        ILogger<InboundUpsertExecutor> logger)
    {
        _db = db;
        _user = user;
        _company = company;
        _logger = logger;
    }

    public async Task<UpsertResultDto> ExecuteAsync(
        SharedEndpoint endpoint,
        string companyCode,
        IReadOnlySet<Guid> boundCompanyIds,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<IAppDbContext, Guid, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct)
    {
        var entityName = InboundUpsertSupport.EntityName(endpoint);
        return await RunCompanyScopedAsync(
            entityName, companyCode, boundCompanyIds, idempotencyKey, received, canonicalRows, codes, requestPayload,
            // Share-aware masters normalize CompanyCode → source company (e.g. 3000 → 2000) before the upsert.
            normalize: companyId => _company.ResolveSource(endpoint, companyId),
            // The share-aware canonical hash folds the source company in.
            hash: (sourceId, rows) => InboundUpsertSupport.CanonicalHash(endpoint, sourceId, rows),
            upsertAsync, ct);
    }

    /// <summary>
    /// R4 (2026-06-22) — Module 5 / Increment D. Company-scoped inbound executor for the TRANSACTIONAL entities
    /// (Grn/Payment/InvoiceStatus). Mirrors the <see cref="SharedEndpoint"/> overload — company resolution,
    /// anti-spoof, endpoint gate, canonical-hash idempotency, transactional upsert + SyncLog/IntegrationError +
    /// endpoint-session telemetry — but does NOT apply share-group <c>ResolveSource</c>: these rows belong to the
    /// literal resolved company (they hang off live PO/invoice transactions, not shared reference data). The
    /// anti-spoof check therefore requires the resolved company itself to be in the key's bound set.
    /// </summary>
    public async Task<UpsertResultDto> ExecuteAsync(
        TransactionalInboundEntity endpoint,
        string companyCode,
        IReadOnlySet<Guid> boundCompanyIds,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<IAppDbContext, Guid, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct)
    {
        var entityName = InboundUpsertSupport.EntityName(endpoint);
        return await RunCompanyScopedAsync(
            entityName, companyCode, boundCompanyIds, idempotencyKey, received, canonicalRows, codes, requestPayload,
            // NOT share-aware — the transactional row belongs to the literal resolved company (no 3000 → 2000).
            normalize: companyId => companyId,
            hash: (sourceId, rows) => InboundUpsertSupport.CanonicalHash($"{entityName}|{sourceId:N}", rows),
            upsertAsync, ct);
    }

    /// <summary>
    /// Shared company-scoped core (steps 1–7). The two public overloads differ only in EntityName, the
    /// <paramref name="normalize"/> (share-group ResolveSource vs identity) and the <paramref name="hash"/>
    /// (share-aware vs transactional canonical hash). Keeps the SharedEndpoint and TransactionalInboundEntity
    /// paths in lock-step.
    /// </summary>
    private async Task<UpsertResultDto> RunCompanyScopedAsync(
        string entityName,
        string companyCode,
        IReadOnlySet<Guid> boundCompanyIds,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<Guid, Guid?> normalize,
        Func<Guid, IEnumerable<string>, string> hash,
        Func<IAppDbContext, Guid, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct)
    {
        // Feature D — sync-log entity id + payload. Computed once and stamped on both the success and the
        // failure log writes. EntityId capped to 400 chars; PayloadJson guarded to <= 64 KB.
        var entityId = InboundUpsertSupport.JoinCodes(codes);
        var payloadJson = InboundUpsertSupport.SerializePayloadCapped(requestPayload);
        var tenantId = _user.TenantId
            ?? throw new ForbiddenException("API key has no tenant context.");

        var incoming = (companyCode ?? string.Empty).Trim();

        // 1. Resolve CompanyCode → TenantEntityId within the key's tenant. IgnoreQueryFilters because the
        //    company filter is not meaningful for the service principal; restrict by tenant explicitly.
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.TenantId == tenantId && e.Code == incoming)
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);

        if (company is null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["companyCode"] = new[] { $"Unknown company code '{incoming}' for this tenant." }
            });

        // 2. Normalize. Share-aware masters fold to the share-group source (3000 → 2000); transactional
        //    entities stay on the literal resolved company.
        var sourceId = normalize(company.Id)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["companyCode"] = new[] { "Could not resolve a source company for the supplied company code." }
            });

        // 3. Anti-spoof: the resolved source MUST be in the key's bound source-company set (Feature C —
        //    multi-company keys). A 2000-bound key accepts 2000/3000/4000 (all resolve to 2000); a key
        //    bound to {2000,5000} additionally accepts 5000/6000 (resolve to 5000).
        if (boundCompanyIds is null || boundCompanyIds.Count == 0 || !boundCompanyIds.Contains(sourceId))
            throw new ForbiddenException(
                "The supplied company resolves to a source company that this API key is not bound to.");

        // 4. Endpoint gate (kill-switch). Missing or disabled inbound map ⇒ reject.
        var map = await _db.InforEndpointMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => !m.IsDeleted
                                      && m.TenantId == tenantId
                                      && m.EntityName == entityName
                                      && m.Direction == SyncDirection.Inbound, ct);
        if (map is null || !map.IsEnabled)
            throw new ForbiddenException($"The inbound '{entityName}' endpoint is not enabled for this tenant.");

        // 5. Idempotency. Prefer the supplied header; otherwise hash the canonical payload + source.
        var effectiveKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? hash(sourceId, canonicalRows)
            : idempotencyKey.Trim();

        var priorSuccess = await _db.InforSyncLogs.IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId
                           && l.EntityName == entityName
                           && l.Direction == SyncDirection.Inbound
                           && l.Status == SyncStatus.Success
                           && l.IdempotencyKey == effectiveKey, ct);
        if (priorSuccess)
        {
            // Replay — return a no-op result. Every row is reported Skipped so the caller sees the count.
            return new UpsertResultDto(incoming, received, 0, 0, received, 0,
                Array.Empty<RowResult>());
        }

        // 6/7. Transactional upsert + SyncLog/IntegrationError + endpoint session update.
        var now = DateTime.UtcNow;
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var upsertResult = await upsertAsync(_db, tenantId, sourceId, ct);

            // Mutable working copy — the resilient flush below may flip individual rows to Failed when an
            // UNANTICIPATED DB constraint (CHECK / unique / NOT NULL only surfaced at SaveChanges) poisons them.
            var rows = upsertResult.ToList();

            // RESILIENT FLUSH (the FAST path is still a single SaveChanges). On a DbUpdateException the offending
            // entr(ies) are isolated via ex.Entries: detached, mapped back to their business code, recorded as a
            // Failed RowResult with the SQL root cause, then the remaining rows are re-flushed (SQL SAVEPOINT +
            // rollback-to-savepoint keeps the open transaction usable). The good rows persist; the poison rows do not.
            await FlushUpsertWithPoisonIsolationAsync(tx, rows, ct);

            var inserted = rows.Count(r => r.Outcome == RowOutcome.Inserted);
            var updated = rows.Count(r => r.Outcome == RowOutcome.Updated);
            var skipped = rows.Count(r => r.Outcome == RowOutcome.Skipped);
            var failed = rows.Count(r => r.Outcome == RowOutcome.Failed);
            var overallSuccess = failed == 0;

            // Structured FAILURES JSON: [{ code, error }] for ALL Failed rows (anticipated + isolated poison). Stored
            // on the linked IntegrationError.StackTrace (the natural detail home) so it is visible from the SyncLog.
            var failuresJson = overallSuccess ? null : BuildFailuresJson(rows);

            var log = new InforSyncLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntityName = entityName,
                Direction = SyncDirection.Inbound,
                Status = overallSuccess ? SyncStatus.Success : SyncStatus.Failed,
                PayloadRef = $"recv:{incoming};src:{sourceId};count:{received}",
                EntityId = entityId,
                EntityCount = received,
                PayloadJson = payloadJson,
                IdempotencyKey = effectiveKey,
                SyncedAt = now,
                // The actual per-row reasons inline (the first few) so the Sync Log shows WHY it failed without a
                // drill-in; the full structured detail still lives on the linked IntegrationError's failures JSON.
                ErrorMessage = overallSuccess
                    ? null
                    : $"{failed} of {received} failed — {BuildFailuresSummary(rows)}",
                CreatedBy = "infor:inbound",
                CreatedOn = now
            };
            _db.InforSyncLogs.Add(log);

            if (!overallSuccess)
            {
                _db.IntegrationErrors.Add(new IntegrationError
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SyncLogId = log.Id,
                    EntityName = entityName,
                    ErrorMessage = $"Inbound {entityName} upsert: {failed} of {received} rows failed.",
                    // The failures JSON is the per-row root cause, viewable from the SyncLog via this linked error.
                    StackTrace = failuresJson,
                    RetryCount = 0,
                    IsResolved = false,
                    CreatedBy = "infor:inbound",
                    CreatedOn = now
                });
            }

            // Endpoint session telemetry — updated in the same transaction.
            map.LastReceivedAt = now;
            map.LastStatus = overallSuccess ? SyncStatus.Success.ToString() : SyncStatus.Failed.ToString();
            map.LastIdempotencyKey = effectiveKey;
            map.LastMessage = overallSuccess
                ? $"Received {received} {entityName} record(s) from company {incoming} (source {sourceId})."
                : $"Received {received} {entityName} record(s); succeeded {received - failed}, {failed} failed.";
            map.ReceivedCount += received;
            map.UpdatedBy = "infor:inbound";
            map.UpdatedOn = now;

            // The SyncLog/IntegrationError + endpoint telemetry are clean writes (no business constraints) — a plain
            // flush. The poison rows were already isolated above, so this cannot re-trip the same DbUpdateException.
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new UpsertResultDto(incoming, received, inserted, updated, skipped, failed, rows);
        }
        catch (ValidationException) { await tx.RollbackAsync(ct); throw; }
        catch (ForbiddenException) { await tx.RollbackAsync(ct); throw; }
        catch (PoisonIsolationImpossibleException pix)
        {
            // The resilient flush could not isolate the poison row(s) (ex.Entries was empty, or the savepoint
            // could not be rolled back / the progress guard tripped). Do NOT 500 and lose everything: roll the
            // whole batch back, then record it as failed (failed=received) and return a normal 200 + failure report.
            await tx.RollbackAsync(ct);
            _logger.LogError(pix.InnerException ?? pix,
                "Inbound {EntityName} upsert: poison-row isolation impossible for tenant {TenantId}, company {CompanyCode}; recording the whole batch as failed (200, not 500).",
                entityName, tenantId, incoming);

            var batchFailures = BuildBatchFailedJson(codes, pix.RootCause);
            await RecordBatchFailureAsync(entityName, tenantId, entityId, payloadJson, effectiveKey,
                $"recv:{incoming};src:{sourceId};count:{received}", received, pix.RootCause, batchFailures, ct);

            // Every row reported Failed so the caller sees an accurate count; HTTP 200 with the failure report.
            var failedRows = codes
                .Select(c => new RowResult((c ?? string.Empty).Trim(), RowOutcome.Failed, pix.RootCause))
                .ToList();
            return new UpsertResultDto(incoming, received, 0, 0, 0, received, failedRows);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);

            // Review S1 — a concurrent/duplicate inbound delivery that lost an optimistic-concurrency or a
            // unique-index race (e.g. the loser of the GRN auto-post claim or the outbox enqueue) is a RETRYABLE
            // per-row skip, NOT a 500 that nukes the whole batch. Record a Failed SyncLog/IntegrationError for
            // visibility and return all-Skipped (200) so LN re-delivers later instead of retrying the entire batch.
            if (InboundUpsertSupport.IsRetryableConcurrencyOrUniqueViolation(ex))
            {
                _logger.LogWarning(ex,
                    "Inbound {EntityName} upsert hit a retryable write conflict (concurrency/unique) for tenant {TenantId}, company {CompanyCode}; reporting Skipped for LN re-delivery.",
                    entityName, tenantId, incoming);
                await RecordRetryableConflictAsync(entityName, tenantId, entityId, payloadJson, effectiveKey,
                    $"recv:{incoming};src:{sourceId};count:{received}", received, ex, ct);
                return new UpsertResultDto(incoming, received, 0, 0, received, 0, Array.Empty<RowResult>());
            }

            _logger.LogError(ex, "Inbound {EntityName} upsert failed for tenant {TenantId}, company {CompanyCode}.",
                entityName, tenantId, incoming);

            // Best-effort: record a failed SyncLog + IntegrationError OUTSIDE the rolled-back transaction so the
            // operator sees the failure in the retry UI. Clear the change tracker first so the previously-added
            // (and failed) upsert entities are not re-attempted by this fresh SaveChanges.
            try
            {
                _db.ClearChangeTracker();
                var failLog = new InforSyncLog
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    EntityName = entityName,
                    Direction = SyncDirection.Inbound,
                    Status = SyncStatus.Failed,
                    PayloadRef = $"recv:{incoming};src:{sourceId};count:{received}",
                    EntityId = entityId,
                    EntityCount = received,
                    PayloadJson = payloadJson,
                    IdempotencyKey = effectiveKey,
                    SyncedAt = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    CreatedBy = "infor:inbound",
                    CreatedOn = DateTime.UtcNow
                };
                _db.InforSyncLogs.Add(failLog);
                _db.IntegrationErrors.Add(new IntegrationError
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SyncLogId = failLog.Id,
                    EntityName = entityName,
                    ErrorMessage = $"Inbound {entityName} upsert aborted: {ex.Message}",
                    StackTrace = ex.StackTrace,
                    RetryCount = 0,
                    IsResolved = false,
                    CreatedBy = "infor:inbound",
                    CreatedOn = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "Failed to record the inbound failure SyncLog/IntegrationError.");
            }

            throw;
        }
    }

    /// <summary>
    /// Review S1 — best-effort, OUTSIDE the rolled-back transaction, record a Failed SyncLog + a RETRYABLE
    /// IntegrationError for a concurrency/unique write conflict (the row is skipped + reported to LN as Skipped, so
    /// LN re-delivers). Clears the change tracker first so the previously-added (and rolled-back) entities are not
    /// re-attempted by this fresh SaveChanges. Never throws.
    /// </summary>
    private async Task RecordRetryableConflictAsync(
        string entityName, Guid tenantId, string? entityId, string? payloadJson, string effectiveKey,
        string payloadRef, int received, Exception ex, CancellationToken ct)
    {
        try
        {
            _db.ClearChangeTracker();
            var failLog = new InforSyncLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntityName = entityName,
                Direction = SyncDirection.Inbound,
                Status = SyncStatus.Failed,
                PayloadRef = payloadRef,
                EntityId = entityId,
                EntityCount = received,
                PayloadJson = payloadJson,
                IdempotencyKey = effectiveKey,
                SyncedAt = DateTime.UtcNow,
                ErrorMessage = $"Retryable write conflict (concurrent/duplicate delivery): {ex.Message}",
                CreatedBy = "infor:inbound",
                CreatedOn = DateTime.UtcNow
            };
            _db.InforSyncLogs.Add(failLog);
            _db.IntegrationErrors.Add(new IntegrationError
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SyncLogId = failLog.Id,
                EntityName = entityName,
                ErrorMessage = $"Inbound {entityName} skipped — retryable write conflict (re-delivery expected): {ex.Message}",
                StackTrace = ex.StackTrace,
                RetryCount = 0,
                IsResolved = false,
                CreatedBy = "infor:inbound",
                CreatedOn = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Failed to record the inbound retryable-conflict SyncLog/IntegrationError.");
        }
    }

    /// <summary>
    /// FIX #1 — resilient flush with poison-row isolation. The FAST path stays a single <see cref="IAppDbContext.SaveChangesAsync"/>;
    /// the slow path engages ONLY on an UNANTICIPATED <see cref="DbUpdateException"/> (a CHECK / unique / NOT-NULL /
    /// FK violation only surfaced at flush). A SQL SAVEPOINT brackets the flush; on a failure we roll BACK to the
    /// savepoint (the open transaction stays usable) and hand off to
    /// <see cref="IsolatePoisonRowsIndividuallyAsync"/>, which re-flushes each pending business entity one at a time so
    /// the provider pinpoints the genuinely-bad rows (a batched MERGE cannot attribute a constraint failure to one
    /// row). The good rows persist; the failing rows are flipped to <see cref="RowOutcome.Failed"/> with the SQL
    /// inner-exception root cause. The retryable concurrency/unique race + the Validation/Forbidden passthroughs are
    /// unaffected: a <see cref="DbUpdateException"/> that
    /// <see cref="InboundUpsertSupport.IsRetryableConcurrencyOrUniqueViolation"/> classifies as the benign enqueue
    /// race is rethrown so the outer catch reports it Skipped for LN re-delivery. When isolation is impossible (no
    /// pending entities / savepoint failure / no single row reproduces the failure) it throws
    /// <see cref="PoisonIsolationImpossibleException"/> so the caller records the whole batch failed and returns 200.
    /// </summary>
    private async Task FlushUpsertWithPoisonIsolationAsync(IDbContextTransaction tx, List<RowResult> rows, CancellationToken ct)
    {
        const string savepoint = "inbound_upsert_flush";

        // ---- FAST path: a single batched SaveChanges. Only a real DB constraint failure engages the slow path. ----
        try
        {
            await tx.CreateSavepointAsync(savepoint, ct);
        }
        catch (Exception spEx)
        {
            throw new PoisonIsolationImpossibleException(
                "Could not create a SQL savepoint to isolate the poison row(s).", spEx, RootCauseOf(spEx));
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return; // happy path — no poison.
        }
        catch (DbUpdateException ex)
        {
            // The benign enqueue/claim race stays a retryable per-row Skip — let the outer catch handle it.
            if (InboundUpsertSupport.IsRetryableConcurrencyOrUniqueViolation(ex))
                throw;

            // The provider (SQL Server) BATCHES the row inserts into a single MERGE, so a constraint failure aborts
            // the whole batch and DbUpdateException.Entries names EVERY participating entry — it does NOT pinpoint the
            // one poison row. Roll back to the savepoint (keeps the transaction usable) and re-flush each business
            // entity INDIVIDUALLY so SQL itself identifies the genuinely-bad rows: the good ones persist, only the
            // truly-failing ones are recorded Failed. This is the slow path; the happy path above stays one batch.
            try
            {
                await tx.RollbackToSavepointAsync(savepoint, ct);
            }
            catch (Exception rbEx)
            {
                throw new PoisonIsolationImpossibleException(
                    "Could not roll back to the savepoint after a flush failure; the transaction is unusable.",
                    rbEx, RootCauseOf(ex));
            }

            await IsolatePoisonRowsIndividuallyAsync(tx, savepoint, rows, RootCauseOf(ex), ex, ct);
        }
    }

    /// <summary>
    /// Slow-path isolation: re-flush each pending business entity ONE AT A TIME (each guarded by its own savepoint)
    /// so the provider pinpoints the genuinely-failing rows even when the original batched MERGE could not. Captures
    /// the business entities currently Added/Modified (the audit-interceptor companions follow their principal and
    /// regenerate per flush), detaches the failed batch's pending changes (PRESERVING the Unchanged endpoint-map row
    /// that the caller mutates after the flush), then replays each entity: a success commits it; a failure marks its
    /// <see cref="RowResult"/> Failed with the DB root cause and discards it. A SAVEPOINT/rollback-to-savepoint
    /// brackets every individual flush so one bad row never poisons the open transaction.
    /// </summary>
    private async Task IsolatePoisonRowsIndividuallyAsync(
        IDbContextTransaction tx, string savepoint, List<RowResult> rows, string batchRootCause,
        DbUpdateException originalEx, CancellationToken ct)
    {
        // Snapshot the pending business entities (skip the interceptor-generated AuditEntry companions — they follow
        // their principal and regenerate per flush). Each tuple carries the entity + the state to replay it under.
        var pending = _db.ChangeTrackerEntries()
            .Where(e => (e.State == EntityState.Added || e.State == EntityState.Modified)
                        && e.Entity.GetType().Name != "AuditEntry")
            .Select(e => (Entity: e.Entity, State: e.State, Code: ResolveBusinessCode(e)))
            .ToList();

        if (pending.Count == 0)
        {
            // Nothing identifiable to replay — cannot isolate. Fall back to a whole-batch failure (200 + report).
            throw new PoisonIsolationImpossibleException(
                "No pending business entities to isolate after a flush failure.", originalEx, batchRootCause);
        }

        // Detach the failed batch's pending changes so the stale MERGE companions don't re-fire — but PRESERVE any
        // Unchanged rows (e.g. the tracked InforEndpointMap the caller mutates AFTER this flush returns; a blanket
        // ClearChangeTracker would silently drop that telemetry update).
        DetachPendingChanges();

        var isolatedAny = false;
        foreach (var (entity, state, code) in pending)
        {
            try
            {
                await tx.CreateSavepointAsync(savepoint, ct);
            }
            catch (Exception spEx)
            {
                throw new PoisonIsolationImpossibleException(
                    "Could not create a per-row savepoint during poison isolation.", spEx, batchRootCause);
            }

            // Re-attach this one entity under its original state and flush it alone.
            var entry = _db.Attach(entity);
            entry.State = state;

            try
            {
                await _db.SaveChangesAsync(ct);
                // Persisted cleanly — detach this row (+ its audit companion) so the tracker stays small for big
                // batches; the next replay starts from just the Unchanged survivors (the endpoint map).
                DetachPendingChanges();
                entry.State = EntityState.Detached;
            }
            catch (DbUpdateException rowEx)
            {
                // This specific row is the (a) poison. Roll back its savepoint, record it Failed, drop it.
                try { await tx.RollbackToSavepointAsync(savepoint, ct); }
                catch (Exception rbEx)
                {
                    throw new PoisonIsolationImpossibleException(
                        "Could not roll back a per-row savepoint during poison isolation; the transaction is unusable.",
                        rbEx, RootCauseOf(rowEx));
                }
                MarkRowFailed(rows, code, RootCauseOf(rowEx));
                DetachPendingChanges();
                try { entry.State = EntityState.Detached; } catch { /* already detached by the rollback */ }
                isolatedAny = true;
            }
        }

        if (!isolatedAny)
        {
            // Every row succeeded individually yet the batch failed — a batch-only effect we cannot attribute to a
            // single row (e.g. an inter-row constraint). Treat as impossible-to-isolate rather than silently commit.
            throw new PoisonIsolationImpossibleException(
                "No single row reproduced the batch failure when flushed individually.", originalEx, batchRootCause);
        }
    }

    /// <summary>
    /// Detach every Added/Modified change-tracker entry (the failed batch's stale entities + their audit companions)
    /// while LEAVING Unchanged rows tracked — notably the InforEndpointMap the caller mutates after the resilient
    /// flush, which a blanket <c>ChangeTracker.Clear()</c> would silently drop.
    /// </summary>
    private void DetachPendingChanges()
    {
        foreach (var e in _db.ChangeTrackerEntries())
            if (e.State is EntityState.Added or EntityState.Modified)
                e.State = EntityState.Detached;
    }

    /// <summary>
    /// Flip the <see cref="RowResult"/> whose <see cref="RowResult.Code"/> matches <paramref name="code"/> to
    /// <see cref="RowOutcome.Failed"/> with <paramref name="error"/>. When the entity could not be mapped back to a
    /// known code (<paramref name="code"/> null/blank or unmatched), the first not-yet-Failed row is failed instead
    /// so the failed COUNT stays accurate even when the precise code is unrecoverable.
    /// </summary>
    private static void MarkRowFailed(List<RowResult> rows, string? code, string error)
    {
        var idx = -1;
        if (!string.IsNullOrWhiteSpace(code))
            idx = rows.FindIndex(r => r.Outcome != RowOutcome.Failed
                                      && string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            idx = rows.FindIndex(r => r.Outcome != RowOutcome.Failed);
        if (idx < 0) return; // every row already Failed — nothing left to flip.

        var existing = rows[idx];
        rows[idx] = existing with { Outcome = RowOutcome.Failed, Error = error };
    }

    /// <summary>
    /// Best-effort map a detached entity back to its business code. Probes the common natural-key properties used
    /// across the inbound entities (Code / PaymentReference / *Number) via the EF metadata; falls back to a stable
    /// "&lt;Type&gt;:&lt;PK&gt;" identifier when none is present.
    /// </summary>
    private static string? ResolveBusinessCode(EntityEntry entry)
    {
        // The candidate natural-key properties, in priority order. These cover Payment(PaymentReference),
        // GoodsReceipt/Asn/Invoice/PurchaseOrder(*Number) and the master terms (Code).
        string[] candidates =
        {
            "Code", "PaymentReference", "GrnNumber", "AsnNumber", "InvoiceNumber", "PoNumber", "Number"
        };

        foreach (var name in candidates)
        {
            var prop = entry.Metadata.FindProperty(name);
            if (prop is null) continue;
            var value = entry.Property(name).CurrentValue;
            var s = value?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        // Stable fallback identifier: "<EntityType>:<primaryKey>".
        var typeName = entry.Metadata.ClrType.Name;
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is not null)
        {
            var keyParts = pk.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "?");
            return $"{typeName}:{string.Join(",", keyParts)}";
        }
        return typeName;
    }

    /// <summary>Unwraps the exception chain to the provider <see cref="SqlException"/> message (the DB root cause).</summary>
    private static string RootCauseOf(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
            if (cur is SqlException sql)
                return sql.Message;
        return ex.InnerException?.Message ?? ex.Message;
    }

    /// <summary>Serialize ALL Failed rows to the structured failures JSON: <c>[{ "code", "error" }, …]</c>.</summary>
    private static string BuildFailuresJson(IEnumerable<RowResult> rows)
    {
        var failures = rows
            .Where(r => r.Outcome == RowOutcome.Failed)
            .Select(r => new { code = r.Code, error = r.Error ?? "unknown error" });
        return JsonSerializer.Serialize(failures);
    }

    /// <summary>Concise human-readable failure summary for InforSyncLog.ErrorMessage — the first few Failed rows'
    /// "code: error" so the Sync Log shows the actual reason inline (the full detail is on the linked
    /// IntegrationError). Capped so a large batch's message stays bounded.</summary>
    private static string BuildFailuresSummary(IReadOnlyCollection<RowResult> rows)
    {
        var failed = rows.Where(r => r.Outcome == RowOutcome.Failed).ToList();
        const int take = 5;
        var head = string.Join("; ", failed.Take(take).Select(r =>
            string.IsNullOrWhiteSpace(r.Code) ? (r.Error ?? "unknown error") : $"{r.Code}: {r.Error ?? "unknown error"}"));
        if (failed.Count > take) head += $"; …(+{failed.Count - take} more)";
        return head.Length > 1000 ? head[..1000] + "…" : head;
    }

    /// <summary>Whole-batch failures JSON (one entry per code) for the isolation-impossible fallback path.</summary>
    private static string BuildBatchFailedJson(IEnumerable<string> codes, string rootCause)
    {
        var failures = codes
            .Select(c => new { code = (c ?? string.Empty).Trim(), error = rootCause });
        return JsonSerializer.Serialize(failures);
    }

    /// <summary>
    /// Isolation-impossible fallback — record a Failed SyncLog (<c>failed=received</c>) + a linked IntegrationError
    /// carrying the whole-batch failures JSON, OUTSIDE the rolled-back transaction. Mirrors
    /// <see cref="RecordRetryableConflictAsync"/> (clears the change tracker first; never throws) but reports the
    /// batch as FAILED (not Skipped) and stores the failures JSON on the IntegrationError detail so it is viewable
    /// from the SyncLog. The caller still returns HTTP 200 with the per-row failure report.
    /// </summary>
    private async Task RecordBatchFailureAsync(
        string entityName, Guid tenantId, string? entityId, string? payloadJson, string effectiveKey,
        string payloadRef, int received, string rootCause, string failuresJson, CancellationToken ct)
    {
        try
        {
            _db.ClearChangeTracker();
            var now = DateTime.UtcNow;
            var failLog = new InforSyncLog
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EntityName = entityName,
                Direction = SyncDirection.Inbound,
                Status = SyncStatus.Failed,
                PayloadRef = payloadRef,
                EntityId = entityId,
                EntityCount = received,
                PayloadJson = payloadJson,
                IdempotencyKey = effectiveKey,
                SyncedAt = now,
                ErrorMessage = $"Succeeded 0 of {received}; {received} failed (see linked error).",
                CreatedBy = "infor:inbound",
                CreatedOn = now
            };
            _db.InforSyncLogs.Add(failLog);
            _db.IntegrationErrors.Add(new IntegrationError
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SyncLogId = failLog.Id,
                EntityName = entityName,
                ErrorMessage = $"Inbound {entityName} upsert: whole batch failed — {rootCause}",
                StackTrace = failuresJson,
                RetryCount = 0,
                IsResolved = false,
                CreatedBy = "infor:inbound",
                CreatedOn = now
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception logEx)
        {
            _logger.LogError(logEx, "Failed to record the inbound whole-batch-failure SyncLog/IntegrationError.");
        }
    }
}

/// <summary>
/// FIX #1 — internal sentinel raised by <see cref="InboundUpsertExecutor"/> when a poison row cannot be isolated
/// from a <see cref="DbUpdateException"/> (no affected entries, a savepoint create/rollback failure, or the progress
/// guard tripped). The executor catches it and records the whole batch as failed, returning a normal 200 +
/// failure report rather than a 500. <see cref="RootCause"/> carries the DB inner-exception message for the report.
/// </summary>
internal sealed class PoisonIsolationImpossibleException : Exception
{
    public string RootCause { get; }

    public PoisonIsolationImpossibleException(string message, Exception? inner, string rootCause)
        : base(message, inner) => RootCause = rootCause;
}
