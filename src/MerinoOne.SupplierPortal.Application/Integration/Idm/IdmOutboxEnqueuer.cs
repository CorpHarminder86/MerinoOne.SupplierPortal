using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm;

/// <inheritdoc />
public sealed class IdmOutboxEnqueuer : IIdmOutboxEnqueuer
{
    // A document has an in-flight/queued op when it carries a Blocked, Pending or InFlight outbox row.
    private static readonly IdmOutboxStatus[] NonTerminal =
        { IdmOutboxStatus.Blocked, IdmOutboxStatus.Pending, IdmOutboxStatus.InFlight };

    public async Task<int> EnqueueOwnerUpdatesAsync(IAppDbContext db, Guid tenantId, string ownerEntityType,
        Guid ownerEntityId, string actor, CancellationToken ct)
    {
        var docs = await db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => !d.IsDeleted && d.TenantId == tenantId
                && d.OwnerEntityType == ownerEntityType && d.OwnerEntityId == ownerEntityId
                && d.Pid != null && d.IdmEntityType != null)
            .ToListAsync(ct);
        if (docs.Count == 0) return 0;

        var docIds = docs.Select(d => d.Id).ToList();
        var busy = (await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => !o.IsDeleted && docIds.Contains(o.DocumentUploadId) && NonTerminal.Contains(o.Status))
            .Select(o => o.DocumentUploadId)
            .ToListAsync(ct))
            .ToHashSet();

        var count = 0;
        foreach (var d in docs)
        {
            if (busy.Contains(d.Id)) continue;
            db.IdmDocumentOutboxes.Add(NewRow(d, IdmOutboxOperation.Update, IdmOutboxStatus.Pending, actor));
            count++;
        }
        return count;
    }

    public async Task<IdmDocumentOutbox?> EnqueueDocumentUpdateAsync(IAppDbContext db, Guid tenantId,
        Guid documentUploadId, string actor, CancellationToken ct)
    {
        var d = await db.DocumentUploads.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == documentUploadId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (d is null || d.Pid is null || d.IdmEntityType is null) return null;

        var busy = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
            .AnyAsync(o => !o.IsDeleted && o.DocumentUploadId == documentUploadId && NonTerminal.Contains(o.Status), ct);
        if (busy) return null;

        var row = NewRow(d, IdmOutboxOperation.Update, IdmOutboxStatus.Pending, actor);
        db.IdmDocumentOutboxes.Add(row);
        return row;
    }

    // Copies the RLS envelope (seccode/tenant/company) + the IDM handle from the owning document — snapshot-on-write.
    private static IdmDocumentOutbox NewRow(DocumentUpload d, IdmOutboxOperation op, IdmOutboxStatus status, string actor) => new()
    {
        DocumentUploadId = d.Id,
        IdmEntityType = d.IdmEntityType!,
        OwnerEntityId = d.OwnerEntityId,
        FileName = d.FileName,
        Operation = op,
        Status = status,
        ExternalId = d.Pid,
        SeccodeId = d.SeccodeId,
        TenantId = d.TenantId,
        TenantEntityId = d.TenantEntityId,
        CreatedBy = actor,
    };
}
