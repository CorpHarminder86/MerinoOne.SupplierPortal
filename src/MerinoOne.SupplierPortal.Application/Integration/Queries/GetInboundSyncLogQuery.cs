using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Queries;

/// <summary>
/// R5 (TSD R5 Addendum §12 / Component 8) — admin read for the inbound <c>proc.SyncLog</c>. Filters by status,
/// entity, external ref, and date range; returns rows with the error message inline. The raw payload is NOT in
/// the list DTO (Failed rows can carry a large nvarchar(max) payload) — it is fetched on demand via
/// <see cref="GetInboundSyncLogPayloadQuery"/>. This is the NEW R5 sync log, distinct from the legacy
/// <c>integration.InforSyncLog</c> read (<see cref="GetSyncLogQuery"/>).
/// </summary>
public record GetInboundSyncLogQuery(
    int Page = 1, int PageSize = 50, string? Status = null, string? EntityType = null,
    string? ExternalRef = null, DateTime? FromDate = null, DateTime? ToDate = null) : IRequest<PagedResult<SyncLogDto>>;

public class GetInboundSyncLogQueryHandler : IRequestHandler<GetInboundSyncLogQuery, PagedResult<SyncLogDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetInboundSyncLogQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PagedResult<SyncLogDto>> Handle(GetInboundSyncLogQuery request, CancellationToken ct)
    {
        // SECURITY: explicit tenant guard. proc.SyncLog is BaseAggregateRoot (seccode-owned by the tenant-admin
        // config seccode), and an admin holds no SecRight on that seccode, so the always-on seccode RLS filter
        // would return empty — bypass it and scope by tenant explicitly.
        var tid = _user.TenantId;
        var q = _db.SyncLogs.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.TenantId == tid);

        if (!string.IsNullOrWhiteSpace(request.Status)) q = q.Where(x => x.Status == request.Status);
        if (!string.IsNullOrWhiteSpace(request.EntityType)) q = q.Where(x => x.EntityType == request.EntityType);
        if (!string.IsNullOrWhiteSpace(request.ExternalRef))
        {
            var r = request.ExternalRef.Trim();
            q = q.Where(x => x.ExternalRef != null && x.ExternalRef.Contains(r));
        }
        if (request.FromDate.HasValue) { var f = request.FromDate.Value.Date; q = q.Where(x => x.ReceivedOn >= f); }
        if (request.ToDate.HasValue) { var t = request.ToDate.Value.Date.AddDays(1); q = q.Where(x => x.ReceivedOn < t); }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.ReceivedOn)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new SyncLogDto(x.Id, x.Seq, x.Direction, x.Api, x.EntityType, x.ExternalRef,
                x.Status, x.ErrorMessage, x.Payload != null, x.ReceivedOn))
            .ToListAsync(ct);

        return new PagedResult<SyncLogDto> { Items = items, Page = request.Page, PageSize = request.PageSize, TotalCount = total };
    }
}

/// <summary>
/// R5 (§12.3) — fetch ONE Sync Log row's raw payload (the drill-in viewer). The list DTO never ships the full
/// payload; this is fetched on demand. Tenant-scoped explicitly (IDOR guard — a caller can only read its own
/// tenant's payloads). Only Failed rows carry a payload (success rows return null).
/// </summary>
public record GetInboundSyncLogPayloadQuery(Guid Id) : IRequest<string?>;

public class GetInboundSyncLogPayloadQueryHandler : IRequestHandler<GetInboundSyncLogPayloadQuery, string?>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetInboundSyncLogPayloadQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<string?> Handle(GetInboundSyncLogPayloadQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.SyncLogs.IgnoreQueryFilters()
            .Where(x => x.Id == request.Id && !x.IsDeleted && x.TenantId == tid)
            .Select(x => x.Payload)
            .FirstOrDefaultAsync(ct);
    }
}
