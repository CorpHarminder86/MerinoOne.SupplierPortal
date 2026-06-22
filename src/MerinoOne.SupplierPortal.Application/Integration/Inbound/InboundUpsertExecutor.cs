using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
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
        IReadOnlyList<RowResult> rows;
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            rows = await upsertAsync(_db, tenantId, sourceId, ct);

            var inserted = rows.Count(r => r.Outcome == RowOutcome.Inserted);
            var updated = rows.Count(r => r.Outcome == RowOutcome.Updated);
            var skipped = rows.Count(r => r.Outcome == RowOutcome.Skipped);
            var failed = rows.Count(r => r.Outcome == RowOutcome.Failed);
            var overallSuccess = failed == 0;

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
                ErrorMessage = overallSuccess ? null : $"{failed} of {received} rows failed.",
                CreatedBy = "infor:inbound",
                CreatedOn = now
            };
            _db.InforSyncLogs.Add(log);

            if (!overallSuccess)
            {
                var detail = string.Join("; ",
                    rows.Where(r => r.Outcome == RowOutcome.Failed)
                        .Select(r => $"{r.Code}: {r.Error}")
                        .Take(20));
                _db.IntegrationErrors.Add(new IntegrationError
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SyncLogId = log.Id,
                    EntityName = entityName,
                    ErrorMessage = $"Inbound {entityName} upsert: {failed} of {received} rows failed.",
                    StackTrace = detail.Length > 0 ? detail : null,
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
                : $"Received {received} {entityName} record(s); {failed} failed.";
            map.ReceivedCount += received;
            map.UpdatedBy = "infor:inbound";
            map.UpdatedOn = now;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new UpsertResultDto(incoming, received, inserted, updated, skipped, failed, rows);
        }
        catch (ValidationException) { await tx.RollbackAsync(ct); throw; }
        catch (ForbiddenException) { await tx.RollbackAsync(ct); throw; }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
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
}
