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

        // Mapped suppliers — resolved CROSS-COMPANY (IgnoreQueryFilters): user↔supplier mapping is tenant-wide
        // admin config, so ALL the user's mappings must show regardless of the header's active company, each
        // with its supplier's company code. (The company filter would otherwise hide mappings in other companies.)
        var rows = await (
            from m in _db.SupplierUserMaps.IgnoreQueryFilters()
            where m.AppUserId == user.Id && !m.IsDeleted
            join s in _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted) on m.SupplierId equals s.Id
            select new { s.Id, s.SupplierCode, s.LegalName, s.TenantEntityId, m.SecRightId })
            .ToListAsync(ct);

        var companyIds = rows.Where(r => r.TenantEntityId.HasValue).Select(r => r.TenantEntityId!.Value).Distinct().ToList();
        var codeMap = (await _db.TenantEntities.IgnoreQueryFilters()
                .Where(te => companyIds.Contains(te.Id))
                .Select(te => new { te.Id, te.Code }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Code);

        var secRightIds = rows.Select(r => r.SecRightId).Distinct().ToList();
        var writeMap = (await _db.SecRights.IgnoreQueryFilters()
                .Where(r => secRightIds.Contains(r.Id))
                .Select(r => new { r.Id, r.CanWrite }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.CanWrite);

        var mapped = rows
            .Select(r => new MappedSupplierDto(
                r.Id, r.SupplierCode, r.LegalName,
                r.TenantEntityId.HasValue && codeMap.TryGetValue(r.TenantEntityId.Value, out var cc) ? cc : "—",
                writeMap.TryGetValue(r.SecRightId, out var cw) && cw))
            .OrderBy(x => x.CompanyCode).ThenBy(x => x.SupplierCode)
            .ToArray();

        var supplierIds = mapped.Select(x => x.SupplierId).ToArray();

        var defaultSeccode = await _db.Seccodes.IgnoreQueryFilters()
            .Where(s => s.AppUserId == user.Id && s.SeccodeType == SeccodeType.U)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        // Company access — the user's UserCompanyMap rows (incl. direct full-access grants), resolved
        // cross-company (IgnoreQueryFilters) + explicit !IsDeleted. Joined to TenantEntities for code/name.
        var companyAccess = await (
            from m in _db.UserCompanyMaps.IgnoreQueryFilters()
            where m.AppUserId == user.Id && !m.IsDeleted
            join te in _db.TenantEntities.IgnoreQueryFilters().Where(te => !te.IsDeleted)
                on m.TenantEntityId equals te.Id
            orderby te.Code
            select new CompanyAccessDto(te.Id, te.Code, te.Name, m.AllSuppliers, m.IsDefault))
            .ToArrayAsync(ct);

        return new UserDetailDto(
            user.Id, user.Seq, user.UserCode, user.FullName, user.Email,
            user.IsInternal, user.IsMfaEnabled, user.IsActive,
            roles, mapped.Length, user.CreatedOn,
            supplierIds, mapped, defaultSeccode ?? Guid.Empty, companyAccess);
    }
}
