using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Queries;

public record GetSyncLogQuery(int Page = 1, int PageSize = 50, string? Status = null, string? EntityName = null) : IRequest<PagedResult<InforSyncLogDto>>;

public class GetSyncLogQueryHandler : IRequestHandler<GetSyncLogQuery, PagedResult<InforSyncLogDto>>
{
    private readonly IAppDbContext _db;
    public GetSyncLogQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<InforSyncLogDto>> Handle(GetSyncLogQuery request, CancellationToken ct)
    {
        var q = _db.InforSyncLogs.AsQueryable();
        if (!string.IsNullOrEmpty(request.Status)) q = q.Where(x => x.Status.ToString() == request.Status);
        if (!string.IsNullOrEmpty(request.EntityName)) q = q.Where(x => x.EntityName == request.EntityName);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.SyncedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new InforSyncLogDto(x.Id, x.Seq, x.EntityName, x.Direction.ToString(),
                x.Status.ToString(), x.PayloadRef, x.IdempotencyKey, x.SyncedAt, x.ErrorMessage))
            .ToListAsync(ct);

        return new PagedResult<InforSyncLogDto> { Items = items, Page = request.Page, PageSize = request.PageSize, TotalCount = total };
    }
}

public record GetIntegrationErrorsQuery(int Page = 1, int PageSize = 50, bool? IsResolved = null) : IRequest<PagedResult<IntegrationErrorDto>>;

public class GetIntegrationErrorsQueryHandler : IRequestHandler<GetIntegrationErrorsQuery, PagedResult<IntegrationErrorDto>>
{
    private readonly IAppDbContext _db;
    public GetIntegrationErrorsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<IntegrationErrorDto>> Handle(GetIntegrationErrorsQuery request, CancellationToken ct)
    {
        var q = _db.IntegrationErrors.AsQueryable();
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
