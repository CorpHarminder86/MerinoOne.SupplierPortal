using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Queries;

public record GetSyncLogQuery(
    int Page = 1, int PageSize = 50, string? Status = null, string? EntityName = null,
    string? Direction = null, DateTime? FromDate = null, DateTime? ToDate = null) : IRequest<PagedResult<InforSyncLogDto>>;

public class GetSyncLogQueryHandler : IRequestHandler<GetSyncLogQuery, PagedResult<InforSyncLogDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetSyncLogQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PagedResult<InforSyncLogDto>> Handle(GetSyncLogQuery request, CancellationToken ct)
    {
        // SECURITY: explicit tenant guard — do NOT rely solely on the (fail-open) global query filter.
        var tid = _user.TenantId;
        var q = _db.InforSyncLogs.Where(x => x.TenantId == tid);
        if (!string.IsNullOrEmpty(request.Status)) q = q.Where(x => x.Status.ToString() == request.Status);
        if (!string.IsNullOrEmpty(request.EntityName)) q = q.Where(x => x.EntityName == request.EntityName);
        if (!string.IsNullOrEmpty(request.Direction)) q = q.Where(x => x.Direction.ToString() == request.Direction);
        if (request.FromDate.HasValue) { var f = request.FromDate.Value.Date; q = q.Where(x => x.SyncedAt >= f); }
        if (request.ToDate.HasValue) { var t = request.ToDate.Value.Date.AddDays(1); q = q.Where(x => x.SyncedAt < t); }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.SyncedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new InforSyncLogDto(x.Id, x.Seq, x.EntityName, x.Direction.ToString(),
                x.Status.ToString(), x.PayloadRef, x.IdempotencyKey, x.SyncedAt, x.ErrorMessage,
                x.EntityId, x.EntityCount, x.RetryCount, x.PayloadJson != null))
            .ToListAsync(ct);

        return new PagedResult<InforSyncLogDto> { Items = items, Page = request.Page, PageSize = request.PageSize, TotalCount = total };
    }
}

/// <summary>
/// Enhancement Round 2 / Feature D — fetch ONE sync-log row's stored request JSON (the payload viewer).
/// The list DTO never ships the full JSON; this is fetched on demand. Tenant-filtered (InforSyncLog is
/// ITenantOwned) so no IgnoreQueryFilters is needed — a tenant can only read its own payloads.
/// </summary>
public record GetSyncLogPayloadQuery(Guid Id) : IRequest<string?>;

public class GetSyncLogPayloadQueryHandler : IRequestHandler<GetSyncLogPayloadQuery, string?>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetSyncLogPayloadQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<string?> Handle(GetSyncLogPayloadQuery request, CancellationToken ct)
    {
        // SECURITY: tenant-scope the by-GUID payload fetch (else IDOR — a caller could read another tenant's
        // payload by id if the global filter is off). Explicit guard, not relying on the (fail-open) filter.
        var tid = _user.TenantId;
        return await _db.InforSyncLogs
            .Where(x => x.Id == request.Id && x.TenantId == tid)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
    }
}

public record GetIntegrationErrorsQuery(int Page = 1, int PageSize = 50, bool? IsResolved = null) : IRequest<PagedResult<IntegrationErrorDto>>;

public class GetIntegrationErrorsQueryHandler : IRequestHandler<GetIntegrationErrorsQuery, PagedResult<IntegrationErrorDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetIntegrationErrorsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PagedResult<IntegrationErrorDto>> Handle(GetIntegrationErrorsQuery request, CancellationToken ct)
    {
        // SECURITY: explicit tenant guard (not relying on the fail-open global filter).
        var tid = _user.TenantId;
        var q = _db.IntegrationErrors.Where(x => x.TenantId == tid);
        if (request.IsResolved.HasValue) q = q.Where(x => x.IsResolved == request.IsResolved.Value);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedOn)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new IntegrationErrorDto(x.Id, x.Seq, x.SyncLogId, x.EntityName,
                x.ErrorMessage, x.RetryCount, x.LastRetriedAt, x.IsResolved, x.ResolutionNote, x.CreatedOn))
            .ToListAsync(ct);

        return new PagedResult<IntegrationErrorDto> { Items = items, Page = request.Page, PageSize = request.PageSize, TotalCount = total };
    }
}
