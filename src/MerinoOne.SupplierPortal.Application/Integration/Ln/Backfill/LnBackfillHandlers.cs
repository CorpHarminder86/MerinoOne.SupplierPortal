using System.Text.Json;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln.Backfill;

// ── R9 (TSD R9 §2.5, D-R9-10/10a/19) — backfill: gate-change propagation. Dry-run is MANDATORY; apply is
// a separate deliberate action that refuses a stale gateVersion. `Sending` untouched (the dispatch-time
// re-check resolves it); Dispatched/Acked immutable (LN cannot be unposted). ─────────────────────────────

/// <summary>Compute the delta preview for a config's current gate and persist it as a DryRun row.</summary>
public record RunLnBackfillDryRunCommand(Guid ConfigId) : IRequest<LnBackfillPreviewDto>;

public class RunLnBackfillDryRunCommandHandler : IRequestHandler<RunLnBackfillDryRunCommand, LnBackfillPreviewDto>
{
    private const int MaxScan = 2000;

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnGateScanner _scanner;
    private readonly ILnEligibilityService _eligibility;
    public RunLnBackfillDryRunCommandHandler(IAppDbContext db, ICurrentUser user, ILnGateScanner scanner, ILnEligibilityService eligibility)
    { _db = db; _user = user; _scanner = scanner; _eligibility = eligibility; }

    public async Task<LnBackfillPreviewDto> Handle(RunLnBackfillDryRunCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        var config = await _db.LnEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct)
            ?? throw new ValidationException("Endpoint config not found.");
        if (config.DispatchMode == LnDispatchMode.Legacy || string.IsNullOrWhiteSpace(config.EligibilityGateExpr))
            throw new ValidationException("Backfill applies to gated configs only (dispatch mode Dynamic/Held with a gate expression).");
        if (string.IsNullOrWhiteSpace(config.CandidateFilterName))
            throw new ValidationException("Backfill requires a candidate filter (the entity-side scan's SQL pre-filter).");

        // Entity-side scan (the SAME scanner the sweep uses — O-R9-9): eligible-but-missing → Enqueue;
        // eligible with a Skipped/Failed row → Re-arm.
        var verdicts = await _scanner.ScanAsync(config, MaxScan, ct);
        var enqueue = verdicts
            .Where(v => v.Eligible && v.ExistingRowStatus is null)
            .Select(v => new LnBackfillRowDto(null, v.EntityId, v.DeterministicKey, null, "eligible, never enqueued"))
            .ToList();
        var rearm = verdicts
            .Where(v => v.Eligible && v.ExistingRowStatus is nameof(OutboxStatus.Skipped) or nameof(OutboxStatus.Failed))
            .Select(v => new LnBackfillRowDto(v.ExistingRowId, v.EntityId, v.DeterministicKey, v.ExistingRowStatus,
                $"eligible under gate v{config.GateVersion} — re-arm same key (LN dedupes)"))
            .ToList();

        // Outbox-side scan: every live Pending/Failed row re-evaluated INDIVIDUALLY (never inferred from
        // filter absence — an under-inclusive filter must not cause a wrong withdraw).
        var liveRows = await _db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tid && m.TransactionType == config.TransactionType && !m.IsDeleted)
            .Select(m => new { m.Id, m.EntityId, m.DeterministicKey, m.Status })
            .ToListAsync(ct);

        var withdraw = new List<LnBackfillRowDto>();
        foreach (var row in liveRows.Where(r => r.Status is OutboxStatus.Pending or OutboxStatus.Failed))
        {
            if (row.EntityId is not { } entityId) continue;
            var gate = await _eligibility.EvaluateAsync(tid, config.TransactionType, entityId, null, ct);
            if (gate.HasGate && !gate.Eligible)
                withdraw.Add(new LnBackfillRowDto(row.Id, entityId, row.DeterministicKey, row.Status.ToString(),
                    gate.Reason ?? "gate returned false"));
        }

        var preview = new LnBackfillPreviewDto(
            Guid.Empty, config.Id, config.TransactionType, config.GateVersion,
            enqueue, rearm, withdraw,
            SendingInFlight: liveRows.Count(r => r.Status == OutboxStatus.Sending),
            PostedImmutable: liveRows.Count(r => r.Status is OutboxStatus.Dispatched or OutboxStatus.Acked),
            ComputedAt: DateTime.UtcNow);

        // Supersede older previews; persist this one (the audit trail for the apply that may follow).
        var now = DateTime.UtcNow;
        await _db.LnBackfillRuns.IgnoreQueryFilters()
            .Where(r => r.LnEndpointConfigId == config.Id && r.Status == "DryRun" && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Superseded")
                .SetProperty(r => r.UpdatedBy, _user.UserCode)
                .SetProperty(r => r.UpdatedOn, now), ct);

        var run = new LnBackfillRun
        {
            TenantId = tid,
            LnEndpointConfigId = config.Id,
            TransactionType = config.TransactionType,
            GateVersion = config.GateVersion,
            Status = "DryRun",
            EnqueueCount = enqueue.Count,
            RearmCount = rearm.Count,
            WithdrawCount = withdraw.Count,
            DryRunResultJson = JsonSerializer.Serialize(preview),
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };
        _db.LnBackfillRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        return preview with { RunId = run.Id };
    }
}

/// <summary>
/// Apply a previewed run (deliberate, never one-click): conditional status-guarded updates make every
/// dispatcher race VISIBLE (RacedAway / EscapedToSending) instead of silent; the dispatch-time re-check
/// (D-R9-9) guarantees eventual consistency for rows that escaped to Sending mid-apply.
/// </summary>
public record ApplyLnBackfillCommand(Guid RunId) : IRequest<LnBackfillApplyResultDto>;

public class ApplyLnBackfillCommandHandler : IRequestHandler<ApplyLnBackfillCommand, LnBackfillApplyResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public ApplyLnBackfillCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<LnBackfillApplyResultDto> Handle(ApplyLnBackfillCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        var run = await _db.LnBackfillRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == request.RunId && r.TenantId == tid && !r.IsDeleted, ct)
            ?? throw new ValidationException("Backfill run not found.");
        if (run.Status != "DryRun")
            throw new ValidationException($"Run is '{run.Status}' — only a fresh DryRun can be applied.");

        var config = await _db.LnEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == run.LnEndpointConfigId && !c.IsDeleted, ct)
            ?? throw new ValidationException("Endpoint config no longer exists.");
        if (config.GateVersion != run.GateVersion)
            throw new ValidationException(
                $"The gate moved (config v{config.GateVersion} vs preview v{run.GateVersion}) — re-run the dry-run.");

        var preview = JsonSerializer.Deserialize<LnBackfillPreviewDto>(run.DryRunResultJson)
            ?? throw new ValidationException("Stored dry-run snapshot is unreadable.");

        var now = DateTime.UtcNow;
        int enqueued = 0, rearmed = 0, withdrawn = 0, racedAway = 0, escaped = 0, alreadyLive = 0;

        await using var tx = await _db.BeginTransactionAsync(ct);

        // 1. Enqueue set — re-probe per key (a user action may have raced in since the preview).
        foreach (var rowDto in preview.Enqueue)
        {
            var exists = await _db.OutboxMessages.IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tid && m.DeterministicKey == rowDto.DeterministicKey && !m.IsDeleted, ct);
            if (exists) { alreadyLive++; continue; }

            // InvoicePost — the code-owned guard (c) claim precedes any invoice enqueue, backfill included.
            if (config.TransactionType == OutboxTransactionType.InvoicePost)
            {
                var claimed = await _db.Invoices.IgnoreQueryFilters()
                    .Where(i => i.Id == rowDto.EntityId && i.TenantId == tid
                                && (i.InvoiceStatus == InvoiceStatus.Submitted || i.InvoiceStatus == InvoiceStatus.Matched)
                                && i.ErpPostInitiatedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.ErpPostInitiatedAt, now)
                        .SetProperty(i => i.ErpSyncId, rowDto.DeterministicKey)
                        .SetProperty(i => i.UpdatedBy, "ln-backfill")
                        .SetProperty(i => i.UpdatedOn, now), ct);
                if (claimed != 1) { racedAway++; continue; }
            }

            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                TenantId = tid,
                TransactionType = config.TransactionType,
                EntityName = EntityNameFor(config),
                EntityId = rowDto.EntityId,
                DeterministicKey = rowDto.DeterministicKey,
                Status = OutboxStatus.Pending,
                GateVersion = run.GateVersion,
                CreatedBy = "ln-backfill",
                CreatedOn = now,
            });
            enqueued++;
        }

        // 2. Re-arm set — conditional: only a row STILL Skipped/Failed re-arms (rowcount 0 = raced away).
        foreach (var rowDto in preview.Rearm.Where(r => r.OutboxMessageId is not null))
        {
            var affected = await _db.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.Id == rowDto.OutboxMessageId
                            && (m.Status == OutboxStatus.Skipped || m.Status == OutboxStatus.Failed))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.SkipReason, (string?)null)
                    .SetProperty(m => m.ErrorClass, (string?)null)
                    .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                    .SetProperty(m => m.GateVersion, run.GateVersion)
                    .SetProperty(m => m.UpdatedBy, "ln-backfill")
                    .SetProperty(m => m.UpdatedOn, now), ct);
            if (affected == 1) rearmed++; else racedAway++;
        }

        // 3. Withdraw set — conditional WHERE status IN (Pending, Failed): a row the dispatcher claimed to
        // Sending mid-apply ESCAPES here and the dispatch-time re-check (D-R9-9) lands it Skipped instead.
        foreach (var rowDto in preview.Withdraw.Where(r => r.OutboxMessageId is not null))
        {
            var affected = await _db.OutboxMessages.IgnoreQueryFilters()
                .Where(m => m.Id == rowDto.OutboxMessageId
                            && (m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Failed))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxStatus.Skipped)
                    .SetProperty(m => m.SkipReason, $"backfill v{run.GateVersion}: {rowDto.Reason ?? "gate returned false"}")
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.ErrorClass, (string?)null)
                    .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                    .SetProperty(m => m.GateVersion, run.GateVersion)
                    .SetProperty(m => m.UpdatedBy, "ln-backfill")
                    .SetProperty(m => m.UpdatedOn, now), ct);
            if (affected == 1) withdrawn++; else escaped++;
        }

        var result = new LnBackfillApplyResultDto(run.Id, enqueued, rearmed, withdrawn, racedAway, escaped, alreadyLive);
        run.Status = "Applied";
        run.AppliedOn = now;
        run.AppliedBy = _user.UserCode;
        run.ApplyResultJson = JsonSerializer.Serialize(result);
        run.UpdatedBy = _user.UserCode;
        run.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Outbox entityName per config — PoNegotiation enqueues under EntityName=PurchaseOrder (legacy contract).</summary>
    private static string EntityNameFor(LnEndpointConfig config) => config.PortalEntity switch
    {
        LnPortalEntity.PoNegotiation => OutboxEntity.PurchaseOrder,
        LnPortalEntity.Invoice => OutboxEntity.Invoice,
        LnPortalEntity.Asn => OutboxEntity.Asn,
        LnPortalEntity.PurchaseOrder => OutboxEntity.PurchaseOrder,
        LnPortalEntity.Supplier => OutboxEntity.Supplier,
        LnPortalEntity.SupplierChange => OutboxEntity.SupplierChange,
        _ => config.PortalEntity,
    };
}

/// <summary>Backfill status per config — drives the D-R9-19 auto-prompt banner (apply stays manual, always).</summary>
public record GetLnBackfillStatusQuery(Guid ConfigId) : IRequest<LnBackfillStatusDto>;

public class GetLnBackfillStatusQueryHandler : IRequestHandler<GetLnBackfillStatusQuery, LnBackfillStatusDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetLnBackfillStatusQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<LnBackfillStatusDto> Handle(GetLnBackfillStatusQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId ?? throw new ValidationException("No tenant context.");
        var config = await _db.LnEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct)
            ?? throw new ValidationException("Endpoint config not found.");

        var runs = await _db.LnBackfillRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.LnEndpointConfigId == config.Id && !r.IsDeleted)
            .OrderByDescending(r => r.CreatedOn)
            .Take(20)
            .ToListAsync(ct);

        var lastApplied = runs.Where(r => r.Status == "Applied").MaxBy(r => r.AppliedOn)?.GateVersion;
        var latestDryRun = runs.FirstOrDefault(r => r.Status == "DryRun" && r.GateVersion == config.GateVersion);
        var gated = config.DispatchMode != LnDispatchMode.Legacy && !string.IsNullOrWhiteSpace(config.EligibilityGateExpr);

        return new LnBackfillStatusDto(
            config.Id, config.TransactionType, config.GateVersion,
            lastApplied,
            runs.FirstOrDefault(r => r.Status is "DryRun" or "Applied")?.CreatedOn,
            latestDryRun?.Id,
            PromptDryRun: gated && config.GateVersion > (lastApplied ?? 0) && latestDryRun is null);
    }
}

/// <summary>Backfill run history (monitoring list).</summary>
public record GetLnBackfillRunsQuery(Guid? ConfigId) : IRequest<IReadOnlyList<LnBackfillRunDto>>;

public class GetLnBackfillRunsQueryHandler : IRequestHandler<GetLnBackfillRunsQuery, IReadOnlyList<LnBackfillRunDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetLnBackfillRunsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<LnBackfillRunDto>> Handle(GetLnBackfillRunsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.LnBackfillRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == tid && !r.IsDeleted
                        && (request.ConfigId == null || r.LnEndpointConfigId == request.ConfigId))
            .OrderByDescending(r => r.CreatedOn)
            .Take(50)
            .Select(r => new LnBackfillRunDto(r.Id, r.TransactionType, r.GateVersion, r.Status,
                r.EnqueueCount, r.RearmCount, r.WithdrawCount, r.CreatedOn, r.CreatedBy, r.AppliedOn, r.AppliedBy))
            .ToListAsync(ct);
    }
}

/// <summary>The full stored preview of one run (row lists for the three sets).</summary>
public record GetLnBackfillRunQuery(Guid RunId) : IRequest<LnBackfillPreviewDto>;

public class GetLnBackfillRunQueryHandler : IRequestHandler<GetLnBackfillRunQuery, LnBackfillPreviewDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetLnBackfillRunQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<LnBackfillPreviewDto> Handle(GetLnBackfillRunQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId ?? throw new ValidationException("No tenant context.");
        var run = await _db.LnBackfillRuns.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RunId && r.TenantId == tid && !r.IsDeleted, ct)
            ?? throw new ValidationException("Backfill run not found.");
        var preview = JsonSerializer.Deserialize<LnBackfillPreviewDto>(run.DryRunResultJson)
            ?? throw new ValidationException("Stored dry-run snapshot is unreadable.");
        return preview with { RunId = run.Id };
    }
}
