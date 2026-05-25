using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Users.Queries;

public record GetUserListQuery(string? Search = null, bool? IsInternal = null, bool? IsActive = null)
    : IRequest<List<UserListItemDto>>;

public class GetUserListQueryHandler : IRequestHandler<GetUserListQuery, List<UserListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetUserListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<UserListItemDto>> Handle(GetUserListQuery request, CancellationToken ct)
    {
        // IgnoreQueryFilters bypasses BOTH seccode AND soft-delete; re-apply !IsDeleted manually.
        var q = _db.AppUsers.IgnoreQueryFilters().Where(u => !u.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(u => u.UserCode.Contains(s) || u.FullName.Contains(s) || u.Email.Contains(s));
        }
        if (request.IsInternal.HasValue)
            q = q.Where(u => u.IsInternal == request.IsInternal.Value);
        if (request.IsActive.HasValue)
            q = q.Where(u => u.IsActive == request.IsActive.Value);

        var rows = await q
            .OrderBy(u => u.UserCode)
            .Select(u => new
            {
                u.Id,
                u.Seq,
                u.UserCode,
                u.FullName,
                u.Email,
                u.IsInternal,
                u.IsMfaEnabled,
                u.IsActive,
                u.CreatedOn,
                Roles = _db.UserRoles.IgnoreQueryFilters()
                    .Where(ur => ur.AppUserId == u.Id && !ur.IsDeleted)
                    .Join(_db.Roles.IgnoreQueryFilters().Where(r => !r.IsDeleted),
                          ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                    .ToArray(),
                SupplierMapCount = _db.SupplierUserMaps.IgnoreQueryFilters()
                    .Count(m => m.AppUserId == u.Id && !m.IsDeleted)
            })
            .ToListAsync(ct);

        return rows.Select(r => new UserListItemDto(
            r.Id, r.Seq, r.UserCode, r.FullName, r.Email,
            r.IsInternal, r.IsMfaEnabled, r.IsActive,
            r.Roles, r.SupplierMapCount, r.CreatedOn)).ToList();
    }
}
