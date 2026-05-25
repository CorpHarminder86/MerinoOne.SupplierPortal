using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Roles.Queries;

public record GetRoleByIdQuery(Guid Id) : IRequest<RoleDetailDto>;

public class GetRoleByIdQueryHandler : IRequestHandler<GetRoleByIdQuery, RoleDetailDto>
{
    private readonly IAppDbContext _db;
    public GetRoleByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<RoleDetailDto> Handle(GetRoleByIdQuery request, CancellationToken ct)
    {
        var role = await _db.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == request.Id && !r.IsDeleted, ct)
            ?? throw new NotFoundException("Role", request.Id);

        // IgnoreQueryFilters drops soft-delete too; re-apply !IsDeleted on every join.
        var permissions = await _db.RolePermissions.IgnoreQueryFilters()
            .Where(rp => rp.RoleId == role.Id && !rp.IsDeleted)
            .Join(_db.Permissions.IgnoreQueryFilters().Where(p => !p.IsDeleted),
                  rp => rp.PermissionId, p => p.Id, (rp, p) => p.Code)
            .OrderBy(c => c)
            .ToArrayAsync(ct);

        return new RoleDetailDto(role.Id, role.Seq, role.Name, permissions);
    }
}
