using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm.Queries;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §7.3 / D-R8-13. The unified IDM sync-log list. RLS-scoped by NOT bypassing the global
/// query filters (IdmDocumentOutbox : BaseAggregateRoot carries the seccode/tenant/company envelope copied at
/// enqueue) — one screen serves every role at its own scope. A SQL view would bypass these EF filters, so this is
/// a LINQ query. FileName is read from the denormalized outbox column, so Delete rows (whose DocumentUpload is
/// soft-deleted) still render. Reaped rows (isDeleted=1) are hidden by the soft-delete filter.
/// </summary>
public record GetIdmSyncLogQuery(
    int Page = 1, int PageSize = 50, string? Status = null, string? Operation = null,
    string? IdmEntityType = null, string? FileName = null,
    DateTime? FromDate = null, DateTime? ToDate = null, Guid? SupplierId = null) : IRequest<PagedResult<IdmSyncLogDto>>;

public class GetIdmSyncLogQueryHandler : IRequestHandler<GetIdmSyncLogQuery, PagedResult<IdmSyncLogDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetIdmSyncLogQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PagedResult<IdmSyncLogDto>> Handle(GetIdmSyncLogQuery request, CancellationToken ct)
    {
        // Filters (seccode/tenant/company) apply — RLS scoping. Explicit tenant guard as defence-in-depth.
        var tid = _user.TenantId;
        var q = _db.IdmDocumentOutboxes.Where(x => x.TenantId == tid);

        if (!string.IsNullOrEmpty(request.Status)) q = q.Where(x => x.Status.ToString() == request.Status);
        if (!string.IsNullOrEmpty(request.Operation)) q = q.Where(x => x.Operation.ToString() == request.Operation);
        if (!string.IsNullOrEmpty(request.IdmEntityType)) q = q.Where(x => x.IdmEntityType == request.IdmEntityType);
        if (!string.IsNullOrEmpty(request.FileName)) q = q.Where(x => x.FileName.Contains(request.FileName));
        if (request.FromDate.HasValue) { var f = request.FromDate.Value.Date; q = q.Where(x => x.CreatedOn >= f); }
        if (request.ToDate.HasValue) { var t = request.ToDate.Value.Date.AddDays(1); q = q.Where(x => x.CreatedOn < t); }

        if (request.SupplierId.HasValue)
        {
            var docIds = await DocumentOwnerSupplierResolver.ResolveDocumentIdsForSupplierAsync(_db, request.SupplierId.Value, ct);
            q = q.Where(x => docIds.Contains(x.DocumentUploadId));
        }

        var total = await q.CountAsync(ct);
        var pageRows = await q.OrderByDescending(x => x.Seq)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new
            {
                x.Id, x.Seq, x.DocumentUploadId, x.IdmEntityType, x.OwnerEntityId, x.FileName,
                Operation = x.Operation.ToString(), Status = x.Status.ToString(), x.AttemptCount, x.NextAttemptAt,
                Pid = x.ExternalId, x.LastError, x.CreatedOn, x.UpdatedOn,
                HasRequestSnapshot = x.RequestSnapshotJson != null, HasResponse = x.ResponseJson != null,
            })
            .ToListAsync(ct);

        // Resolve the owning supplier for display (Supplier column, RLS-scoped rows only — see resolver docs).
        // The outbox has no OwnerEntityType column, so join DocumentUpload for just this page's ids. Delete-op
        // rows reference a soft-deleted DocumentUpload by design (snapshot-on-write), so IgnoreQueryFilters +
        // an explicit tenant guard is required here (not a scope leak — the outbox row itself is already RLS-scoped).
        var docUploadIds = pageRows.Select(r => r.DocumentUploadId).Distinct().ToList();
        var docMetaById = await _db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => d.TenantId == tid && docUploadIds.Contains(d.Id))
            .Select(d => new { d.Id, d.OwnerEntityType, d.MimeType, d.FileSizeKb })
            .ToDictionaryAsync(d => d.Id, ct);

        var supplierDisplay = await DocumentOwnerSupplierResolver.ResolveSupplierDisplayAsync(
            _db,
            pageRows.Where(r => docMetaById.ContainsKey(r.DocumentUploadId))
                    .Select(r => (docMetaById[r.DocumentUploadId].OwnerEntityType, r.OwnerEntityId)),
            ct);

        var items = pageRows.Select(r =>
        {
            (string Code, string Name) sup = default;
            string? mimeType = null;
            long? fileSizeKb = null;
            if (docMetaById.TryGetValue(r.DocumentUploadId, out var meta))
            {
                mimeType = meta.MimeType;
                fileSizeKb = meta.FileSizeKb;
                supplierDisplay.TryGetValue((meta.OwnerEntityType, r.OwnerEntityId), out sup);
            }
            return new IdmSyncLogDto(
                r.Id, r.Seq, r.DocumentUploadId, r.IdmEntityType, r.OwnerEntityId, r.FileName,
                r.Operation, r.Status, r.AttemptCount, r.NextAttemptAt, r.Pid,
                r.LastError, r.CreatedOn, r.UpdatedOn, r.HasRequestSnapshot, r.HasResponse, sup.Code, sup.Name,
                mimeType, fileSizeKb);
        }).ToList();

        return new PagedResult<IdmSyncLogDto> { Items = items, Page = request.Page, PageSize = request.PageSize, TotalCount = total };
    }
}

/// <summary>On-demand fetch of one row's elided request snapshot + raw IDM (XML) response. RLS + tenant scoped.</summary>
public record GetIdmSyncLogDetailQuery(Guid Id) : IRequest<IdmSyncLogDetailDto?>;

public class GetIdmSyncLogDetailQueryHandler : IRequestHandler<GetIdmSyncLogDetailQuery, IdmSyncLogDetailDto?>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetIdmSyncLogDetailQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IdmSyncLogDetailDto?> Handle(GetIdmSyncLogDetailQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.IdmDocumentOutboxes
            .Where(x => x.Id == request.Id && x.TenantId == tid)
            .Select(x => new IdmSyncLogDetailDto(x.RequestSnapshotJson, x.ResponseJson))
            .FirstOrDefaultAsync(ct);
    }
}
