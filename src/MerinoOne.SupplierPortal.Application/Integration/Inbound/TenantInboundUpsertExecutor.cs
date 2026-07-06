using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ForbiddenException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ForbiddenException;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Tenant-scoped sibling of <see cref="InboundUpsertExecutor"/> for the reference masters
/// (Currency / Country / State / City / PostalCode). It is the company executor MINUS steps 1–3
/// (company resolution / share-group normalization / anti-spoof): the tenant comes straight from the
/// API-key principal, rows are keyed on (TenantId, Code), and parents are resolved by (TenantId, code).
/// The endpoint gate, idempotency, transactional SyncLog/IntegrationError + endpoint-session telemetry
/// are identical and reuse <see cref="InboundUpsertSupport"/>.
/// </summary>
public class TenantInboundUpsertExecutor
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILogger<TenantInboundUpsertExecutor> _logger;

    public TenantInboundUpsertExecutor(IAppDbContext db, ICurrentUser user, ILogger<TenantInboundUpsertExecutor> logger)
    {
        _db = db;
        _user = user;
        _logger = logger;
    }

    public Task<UpsertResultDto> ExecuteAsync(
        TenantInboundEntity endpoint,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<IAppDbContext, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct)
        => RunAsync(InboundUpsertSupport.EntityName(endpoint), idempotencyKey, received, canonicalRows, codes, requestPayload, upsertAsync, ct);

    /// <summary>
    /// R4 (2026-06-22) — Module 5 / Increment D. Tenant-scoped sibling for the transactional ErpAck endpoint
    /// (/inbound/erp-ack has no CompanyCode — it resolves <c>portalRef</c> → an outbox row in the key's tenant).
    /// Mirrors the <see cref="TenantInboundEntity"/> overload (endpoint gate, idempotency, transactional
    /// SyncLog/IntegrationError + endpoint-session telemetry) keyed on the transactional EntityName.
    /// </summary>
    public Task<UpsertResultDto> ExecuteAsync(
        TransactionalInboundEntity endpoint,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<IAppDbContext, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct,
        // R9 (D-R9-11) — the HeldInboundReplayWorker replays held erp-acks with NO ambient principal;
        // it passes the held row's tenant explicitly. Live traffic never sets this.
        Guid? tenantOverride = null)
        => RunAsync(InboundUpsertSupport.EntityName(endpoint), idempotencyKey, received, canonicalRows, codes, requestPayload, upsertAsync, ct, tenantOverride);

    private async Task<UpsertResultDto> RunAsync(
        string entityName,
        string? idempotencyKey,
        int received,
        IEnumerable<string> canonicalRows,
        IEnumerable<string> codes,
        object requestPayload,
        Func<IAppDbContext, Guid, CancellationToken, Task<IReadOnlyList<RowResult>>> upsertAsync,
        CancellationToken ct,
        Guid? tenantOverride = null)
    {
        var entityId = InboundUpsertSupport.JoinCodes(codes);
        var payloadJson = InboundUpsertSupport.SerializePayloadCapped(requestPayload);
        var tenantId = tenantOverride ?? _user.TenantId ?? throw new ForbiddenException("API key has no tenant context.");

        // Endpoint gate (kill-switch). Missing or disabled inbound map ⇒ reject.
        var map = await _db.InforEndpointMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => !m.IsDeleted
                                      && m.TenantId == tenantId
                                      && m.EntityName == entityName
                                      && m.Direction == SyncDirection.Inbound, ct);
        if (map is null || !map.IsEnabled)
            throw new ForbiddenException($"The inbound '{entityName}' endpoint is not enabled for this tenant.");

        // Idempotency — supplied header, else hash of canonical payload + tenant.
        var effectiveKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? InboundUpsertSupport.CanonicalHash($"{entityName}|{tenantId:N}", canonicalRows)
            : idempotencyKey.Trim();

        var priorSuccess = await _db.InforSyncLogs.IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId
                           && l.EntityName == entityName
                           && l.Direction == SyncDirection.Inbound
                           && l.Status == SyncStatus.Success
                           && l.IdempotencyKey == effectiveKey, ct);
        if (priorSuccess)
            return new UpsertResultDto(entityName, received, 0, 0, received, 0, Array.Empty<RowResult>());

        var now = DateTime.UtcNow;
        IReadOnlyList<RowResult> rows;
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            rows = await upsertAsync(_db, tenantId, ct);

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
                PayloadRef = $"tenant:{tenantId:N};count:{received}",
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
                    rows.Where(r => r.Outcome == RowOutcome.Failed).Select(r => $"{r.Code}: {r.Error}").Take(20));
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

            map.LastReceivedAt = now;
            map.LastStatus = overallSuccess ? SyncStatus.Success.ToString() : SyncStatus.Failed.ToString();
            map.LastIdempotencyKey = effectiveKey;
            map.LastMessage = overallSuccess
                ? $"Received {received} {entityName} record(s)."
                : $"Received {received} {entityName} record(s); {failed} failed.";
            map.ReceivedCount += received;
            map.UpdatedBy = "infor:inbound";
            map.UpdatedOn = now;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new UpsertResultDto(entityName, received, inserted, updated, skipped, failed, rows);
        }
        catch (ForbiddenException) { await tx.RollbackAsync(ct); throw; }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);

            // Review S1 — a concurrent/duplicate inbound delivery that lost an optimistic-concurrency or unique-index
            // race (e.g. two erp-acks racing the same outbox row) is a RETRYABLE per-row skip, NOT a 500 that nukes
            // the whole batch. Record a Failed SyncLog/IntegrationError and return all-Skipped (200) for re-delivery.
            if (InboundUpsertSupport.IsRetryableConcurrencyOrUniqueViolation(ex))
            {
                _logger.LogWarning(ex,
                    "Inbound {EntityName} upsert hit a retryable write conflict (concurrency/unique) for tenant {TenantId}; reporting Skipped for LN re-delivery.",
                    entityName, tenantId);
                await RecordRetryableConflictAsync(entityName, tenantId, entityId, payloadJson, effectiveKey, received, ex, ct);
                return new UpsertResultDto(entityName, received, 0, 0, received, 0, Array.Empty<RowResult>());
            }

            _logger.LogError(ex, "Inbound {EntityName} upsert failed for tenant {TenantId}.", entityName, tenantId);
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
                    PayloadRef = $"tenant:{tenantId:N};count:{received}",
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
    /// IntegrationError for a concurrency/unique write conflict (the batch is skipped + reported to LN as Skipped,
    /// so LN re-delivers). Never throws.
    /// </summary>
    private async Task RecordRetryableConflictAsync(
        string entityName, Guid tenantId, string? entityId, string? payloadJson, string effectiveKey,
        int received, Exception ex, CancellationToken ct)
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
                PayloadRef = $"tenant:{tenantId:N};count:{received}",
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
}
