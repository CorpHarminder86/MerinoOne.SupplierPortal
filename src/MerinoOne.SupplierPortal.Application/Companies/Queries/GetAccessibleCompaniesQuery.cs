using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Companies;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Companies.Queries;

/// <summary>
/// The CURRENT user's accessible companies — drives the header company selector for EVERY authenticated
/// user (not just admins). A Tenant Admin sees all active companies in the tenant; a regular user (e.g. a
/// supplier user) sees only the companies they're mapped to via UserCompanyMap. This is deliberately NOT
/// gated by Settings.Read (unlike GetCompaniesQuery) — supplier users have no admin permission but still
/// need their selector populated. Tenant scoping is enforced by the always-on tenant filter.
/// </summary>
public record GetAccessibleCompaniesQuery() : IRequest<List<CompanyDto>>;

public class GetAccessibleCompaniesQueryHandler : IRequestHandler<GetAccessibleCompaniesQuery, List<CompanyDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GetAccessibleCompaniesQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<List<CompanyDto>> Handle(GetAccessibleCompaniesQuery request, CancellationToken ct)
    {
        if (_user.TenantId is null) return new();   // Platform Admin / no tenant → no business companies

        // Tenant Admin → every active company in the tenant (implicit-all access).
        if (_user.IsAdmin)
        {
            return await _db.TenantEntities
                .Where(e => e.IsActive)
                .OrderBy(e => e.Code)
                .Select(e => new CompanyDto(e.Id, e.Seq, e.Code, e.Name, e.IsActive, e.CreatedOn))
                .ToListAsync(ct);
        }

        // Regular user → only their mapped companies (UserCompanyMap). Tenant filter scopes both tables.
        var userCode = _user.UserCode;
        if (string.IsNullOrEmpty(userCode)) return new();

        var userId = await _db.AppUsers
            .Where(u => u.UserCode == userCode)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);
        if (userId == Guid.Empty) return new();

        return await (
            from m in _db.UserCompanyMaps.Where(m => !m.IsDeleted && m.AppUserId == userId)
            join e in _db.TenantEntities.Where(e => e.IsActive) on m.TenantEntityId equals e.Id
            orderby e.Code
            select new CompanyDto(e.Id, e.Seq, e.Code, e.Name, e.IsActive, e.CreatedOn))
            .ToListAsync(ct);
    }
}
