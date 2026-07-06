using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

/// <summary>
/// R9 — <see cref="ILnGatedOutboxEnqueuer"/>. Wraps the classic enqueue with (1) the config gate and
/// (2) re-arm-over-create on Skipped/Failed keys. The re-arm is a TRACKED mutation (not ExecuteUpdate)
/// so it commits atomically with the caller's SaveChanges and the rowVersion concurrency token
/// arbitrates against a concurrent dispatcher/backfill touch (a loser surfaces as the caller's normal
/// concurrency conflict instead of silently clobbering a claim).
/// </summary>
public sealed class LnGatedOutboxEnqueuer : ILnGatedOutboxEnqueuer
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnEligibilityService _eligibility;
    private readonly ILogger<LnGatedOutboxEnqueuer> _logger;

    public LnGatedOutboxEnqueuer(IAppDbContext db, ICurrentUser user, ILnEligibilityService eligibility,
        ILogger<LnGatedOutboxEnqueuer> logger)
    {
        _db = db;
        _user = user;
        _eligibility = eligibility;
        _logger = logger;
    }

    public async Task<GatedEnqueueResult> EnqueueAsync(
        string transactionType, string entityName, Guid? entityId, string deterministicKey,
        string? payloadJson, Guid? tenantIdOverride = null, LnInputDocOverrides? overrides = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deterministicKey))
            throw new ArgumentException("deterministicKey is required (and must be reused across retries).", nameof(deterministicKey));

        var tenantId = tenantIdOverride ?? _user.TenantId
            ?? throw new InvalidOperationException(
                "A gated outbox enqueue requires a tenant context (review D2 — pass tenantIdOverride from workers).");

        // 1. Gate (D-R9-8): applies iff a non-Legacy config with a gate expression exists. Ineligible ⇒ NO row —
        //    the business change proceeds, posting is config-suppressed; the sweep catches later eligibility.
        var verdict = LnGateVerdict.NoConfig;
        if (entityId is { } eid)
            verdict = await _eligibility.EvaluateAsync(tenantId, transactionType, eid, overrides, ct);
        if (verdict.HasGate && !verdict.Eligible)
        {
            _logger.LogInformation("[Outbox] Gate withheld {Tx} {Entity}:{Id}: {Reason}",
                transactionType, entityName, entityId, verdict.Reason);
            return new GatedEnqueueResult(GatedEnqueueOutcome.GateIneligible, verdict.GateVersion, verdict.Reason);
        }

        // 2. Staged in THIS unit of work already? (Same defence as the classic OutboxDispatcher.)
        var stagedLocally = _db.OutboxMessages.Local
            .Any(m => m.TenantId == tenantId && m.DeterministicKey == deterministicKey && !m.IsDeleted);
        if (stagedLocally)
            return new GatedEnqueueResult(GatedEnqueueOutcome.AlreadyLive, verdict.GateVersion, "staged in this unit of work");

        // 3. Persisted row on the same (tenant, key)? TRACKED read — a Skipped/Failed row re-arms in place.
        var existing = await _db.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.DeterministicKey == deterministicKey && !m.IsDeleted, ct);

        if (existing is not null)
        {
            if (existing.Status is OutboxStatus.Skipped or OutboxStatus.Failed)
            {
                var priorStatus = existing.Status;
                existing.Status = OutboxStatus.Pending;
                existing.LastError = null;
                existing.SkipReason = null;
                existing.ErrorClass = null;
                existing.DispatchedAt = null;
                existing.GateVersion = verdict.GateVersion ?? existing.GateVersion;
                existing.PayloadJson = payloadJson;   // fresh per-row context (e.g. a new PO-accept proposedDate)
                existing.UpdatedBy = Actor();
                existing.UpdatedOn = DateTime.UtcNow;
                _logger.LogInformation("[Outbox] Re-armed {Status}→Pending {Tx} {Entity}:{Id} key={Key} (D-R9-10a).",
                    priorStatus, transactionType, entityName, entityId, deterministicKey);
                return new GatedEnqueueResult(GatedEnqueueOutcome.Rearmed, verdict.GateVersion, null);
            }
            return new GatedEnqueueResult(GatedEnqueueOutcome.AlreadyLive, verdict.GateVersion, $"row is {existing.Status}");
        }

        // 4. Fresh enqueue.
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,   // explicit — worker callers (sweep) have no ambient principal for the interceptor
            TransactionType = transactionType,
            EntityName = entityName,
            EntityId = entityId,
            DeterministicKey = deterministicKey,
            PayloadJson = payloadJson,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            GateVersion = verdict.GateVersion,
            CreatedBy = Actor(),
            CreatedOn = DateTime.UtcNow,
        });
        var outcome = verdict.HasGate || verdict.GateVersion is not null
            ? GatedEnqueueOutcome.Enqueued
            : GatedEnqueueOutcome.EnqueuedLegacy;
        return new GatedEnqueueResult(outcome, verdict.GateVersion, null);
    }

    private string Actor() => string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
}
