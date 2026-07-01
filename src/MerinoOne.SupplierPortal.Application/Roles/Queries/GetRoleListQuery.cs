using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Roles.Queries;

public record GetRoleListQuery(int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<RoleListItemDto>>;

public class GetRoleListQueryHandler : IRequestHandler<GetRoleListQuery, PagedResult<RoleListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetRoleListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<RoleListItemDto>> Handle(GetRoleListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var baseQ = _db.Roles.IgnoreQueryFilters().Where(r => !r.IsDeleted);
        var total = await baseQ.CountAsync(ct);

        var pageRoles = await baseQ
            .OrderBy(r => r.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new { r.Id, r.Seq, r.Name })
            .ToListAsync(ct);

        var ids = pageRoles.Select(r => r.Id).ToList();

        // Counts via GroupBy aggregate joins scoped to the page's role ids — one grouped pass each, no
        // per-row correlated subquery. UserRole is seekable by RoleId via IX_UserRole_role_user (migration 0041).
        var permCounts = await _db.RolePermissions.IgnoreQueryFilters()
            .Where(rp => !rp.IsDeleted && ids.Contains(rp.RoleId))
            .GroupBy(rp => rp.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

        var userCounts = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => !ur.IsDeleted && ids.Contains(ur.RoleId))
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

        var items = pageRoles.Select(r => new RoleListItemDto(
            r.Id, r.Seq, r.Name,
            permCounts.GetValueOrDefault(r.Id),
            userCounts.GetValueOrDefault(r.Id))).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<RoleListItemDto>(items, page, pageSize, total, totalPages);
    }
}
