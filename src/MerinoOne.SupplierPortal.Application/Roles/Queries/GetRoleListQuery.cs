using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Roles.Queries;

public record GetRoleListQuery() : IRequest<List<RoleListItemDto>>;

public class GetRoleListQueryHandler : IRequestHandler<GetRoleListQuery, List<RoleListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetRoleListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<RoleListItemDto>> Handle(GetRoleListQuery request, CancellationToken ct)
    {
        return await _db.Roles.IgnoreQueryFilters()
            .OrderBy(r => r.Name)
            .Select(r => new RoleListItemDto(
                r.Id,
                r.Seq,
                r.Name,
                _db.RolePermissions.IgnoreQueryFilters().Count(rp => rp.RoleId == r.Id),
                _db.UserRoles.IgnoreQueryFilters().Count(ur => ur.RoleId == r.Id)))
            .ToListAsync(ct);
    }
}
