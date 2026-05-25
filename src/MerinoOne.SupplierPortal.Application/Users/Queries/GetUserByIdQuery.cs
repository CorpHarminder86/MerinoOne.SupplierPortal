using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Users.Queries;

public record GetUserByIdQuery(Guid Id) : IRequest<UserDetailDto>;

public class GetUserByIdQueryHandler : IRequestHandler<GetUserByIdQuery, UserDetailDto>
{
    private readonly IAppDbContext _db;
    public GetUserByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<UserDetailDto> Handle(GetUserByIdQuery request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.Id, ct)
            ?? throw new NotFoundException("User", request.Id);

        // IgnoreQueryFilters() drops the SECCODE filter (admin needs cross-tenant view) but
        // also drops the SOFT-DELETE filter — must re-apply !IsDeleted explicitly or unmapped
        // / removed rows resurface in the UI.
        var roles = await _db.UserRoles.IgnoreQueryFilters()
            .Where(ur => ur.AppUserId == user.Id && !ur.IsDeleted)
            .Join(_db.Roles.IgnoreQueryFilters().Where(r => !r.IsDeleted),
                  ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .ToArrayAsync(ct);

        var supplierIds = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .Where(m => m.AppUserId == user.Id && !m.IsDeleted)
            .Select(m => m.SupplierId)
            .ToArrayAsync(ct);

        var defaultSeccode = await _db.Seccodes.IgnoreQueryFilters()
            .Where(s => s.AppUserId == user.Id && s.SeccodeType == SeccodeType.U)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        return new UserDetailDto(
            user.Id, user.Seq, user.UserCode, user.FullName, user.Email,
            user.IsInternal, user.IsMfaEnabled, user.IsActive,
            roles, supplierIds.Length, user.CreatedOn,
            supplierIds, defaultSeccode ?? Guid.Empty);
    }
}
