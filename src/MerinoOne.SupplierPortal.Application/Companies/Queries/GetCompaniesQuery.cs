using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Companies;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Companies.Queries;

/// <summary>
/// List the current tenant's companies (TenantEntities). The always-on tenant filter on TenantEntity
/// scopes the result to the caller's tenant automatically — no IgnoreQueryFilters here. By default only
/// active companies are returned; pass <c>includeInactive</c> to see deactivated ones (admin views).
/// </summary>
public record GetCompaniesQuery(bool IncludeInactive = false) : IRequest<List<CompanyDto>>;

public class GetCompaniesQueryHandler : IRequestHandler<GetCompaniesQuery, List<CompanyDto>>
{
    private readonly IAppDbContext _db;
    public GetCompaniesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<CompanyDto>> Handle(GetCompaniesQuery request, CancellationToken ct)
    {
        var q = _db.TenantEntities.AsQueryable();
        if (!request.IncludeInactive)
            q = q.Where(e => e.IsActive);

        return await q
            .OrderBy(e => e.Code)
            .Select(e => new CompanyDto(e.Id, e.Seq, e.Code, e.Name, e.IsActive, e.CreatedOn))
            .ToListAsync(ct);
    }
}
