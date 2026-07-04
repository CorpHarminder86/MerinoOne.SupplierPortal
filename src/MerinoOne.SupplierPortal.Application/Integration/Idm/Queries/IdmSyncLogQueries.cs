using MediatR;
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
    DateTime? FromDate = null, DateTime? ToDate = null) : IRequest<PagedResult<IdmSyncLogDto>>;

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

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.Seq)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new IdmSyncLogDto(
                x.Id, x.Seq, x.DocumentUploadId, x.IdmEntityType, x.OwnerEntityId, x.FileName,
                x.Operation.ToString(), x.Status.ToString(), x.AttemptCount, x.NextAttemptAt, x.ExternalId,
                x.LastError, x.CreatedOn, x.UpdatedOn, x.RequestSnapshotJson != null, x.ResponseJson != null))
            .ToListAsync(ct);

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
