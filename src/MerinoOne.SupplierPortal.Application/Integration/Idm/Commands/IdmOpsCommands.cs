using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm.Commands;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §7.3. Re-arm a Failed/Unresolvable IDM outbox row: reset to Pending, clear the backoff
/// and attempt count. Idempotency (correlationId/pid) prevents an IDM duplicate. RLS-scoped (the row must be
/// visible to the caller). This is the ONLY re-arm path for a terminal 4xx row (the seeding scan never re-seeds it).
/// </summary>
public record RetryIdmOutboxRowCommand(Guid Id) : IRequest<bool>;

public class RetryIdmOutboxRowCommandHandler : IRequestHandler<RetryIdmOutboxRowCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public RetryIdmOutboxRowCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(RetryIdmOutboxRowCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var row = await _db.IdmDocumentOutboxes.FirstOrDefaultAsync(x => x.Id == request.Id && x.TenantId == tid, ct);
        if (row is null || (row.Status != IdmOutboxStatus.Failed && row.Status != IdmOutboxStatus.Unresolvable))
            return false;

        row.Status = IdmOutboxStatus.Pending;
        row.NextAttemptAt = null;
        row.AttemptCount = 0;
        row.LastError = null;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// R8 (2026-07-04) — TSD R8 §7.3 / D4a / D-R8-24. Manual "Re-push": for a Success sync-log row whose document is
/// still synced (pid present), enqueue a NEW Update outbox row carrying the pid — never mutate the terminal Create
/// row. Rejects when the source row is not Success, the document is soft-deleted / has no pid, or a non-terminal
/// row already exists in its partition.
/// </summary>
public record RepushIdmDocumentCommand(Guid OutboxRowId) : IRequest<bool>;

public class RepushIdmDocumentCommandHandler : IRequestHandler<RepushIdmDocumentCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IIdmOutboxEnqueuer _enqueuer;
    public RepushIdmDocumentCommandHandler(IAppDbContext db, ICurrentUser user, IIdmOutboxEnqueuer enqueuer)
    { _db = db; _user = user; _enqueuer = enqueuer; }

    public async Task<bool> Handle(RepushIdmDocumentCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;

        // Resolve the source row through RLS (the caller must be able to see it).
        var src = await _db.IdmDocumentOutboxes
            .Where(x => x.Id == request.OutboxRowId && x.TenantId == tid)
            .Select(x => new { x.DocumentUploadId, x.Status })
            .FirstOrDefaultAsync(ct);
        if (src is null || src.Status != IdmOutboxStatus.Success) return false;

        var row = await _enqueuer.EnqueueDocumentUpdateAsync(_db, tid, src.DocumentUploadId, _user.UserCode, ct);
        if (row is null) return false;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// R8 (2026-07-04) — TSD R8 §7.1. Bulk backfill: stamp <c>idmEntityType</c> on existing documents whose type maps
/// to an enabled config and is not yet classified. Convenience over the worker's per-drain stamping — lets an
/// operator classify a large backlog in one action. Tenant-scoped bulk update.
/// </summary>
public record BackfillIdmEntityTypeCommand : IRequest<int>;

public class BackfillIdmEntityTypeCommandHandler : IRequestHandler<BackfillIdmEntityTypeCommand, int>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISnapshotProviderRegistry _providers;
    public BackfillIdmEntityTypeCommandHandler(IAppDbContext db, ICurrentUser user, ISnapshotProviderRegistry providers)
    { _db = db; _user = user; _providers = providers; }

    public async Task<int> Handle(BackfillIdmEntityTypeCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return 0;

        var configs = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && c.IsEnabled && !c.IsDeleted)
            .Select(c => new { c.AttachmentType, c.IdmEntityType })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var total = 0;
        foreach (var cfg in configs)
        {
            // 2026-07-05 fix — mirror the worker's seeding predicate: the mapping is PORTAL-ENTITY-aware, so only
            // stamp documents owned by the provider's entity. Without this, a shared attachment-type code (e.g.
            // "Msme" on both ASNs and supplier onboarding) stamped supplier-owned docs with the ASN entity type,
            // which the dispatcher can never resolve (junk Unresolvable rows).
            var provider = _providers.TryGet(cfg.IdmEntityType);
            if (provider is null) continue;

            total += await _db.DocumentUploads.IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && d.TenantId == tid && d.IdmEntityType == null
                            && d.OwnerEntityType == provider.OwnerEntityType && d.DocumentType == cfg.AttachmentType)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IdmEntityType, cfg.IdmEntityType)
                    .SetProperty(d => d.UpdatedBy, _user.UserCode)
                    .SetProperty(d => d.UpdatedOn, now), ct);
        }
        return total;
    }
}
