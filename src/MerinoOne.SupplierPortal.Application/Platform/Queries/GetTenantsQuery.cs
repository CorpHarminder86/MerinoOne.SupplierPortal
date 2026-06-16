using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Platform;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Platform.Queries;

/// <summary>Platform-Admin: list all tenants (cross-tenant) with their company counts.</summary>
public record GetTenantsQuery(string? Search = null) : IRequest<List<TenantDto>>;

public class GetTenantsQueryHandler : IRequestHandler<GetTenantsQuery, List<TenantDto>>
{
    private readonly IAppDbContext _db;
    public GetTenantsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<TenantDto>> Handle(GetTenantsQuery request, CancellationToken ct)
    {
        // IgnoreQueryFilters: the caller is a Platform Admin (cross-tenant). Re-apply !IsDeleted.
        var q = _db.Tenants.IgnoreQueryFilters().Where(t => !t.IsDeleted);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(t => t.Name.Contains(s));
        }

        var rows = await q
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Seq,
                t.Name,
                t.IsActive,
                t.CreatedOn,
                CompanyCount = _db.TenantEntities.IgnoreQueryFilters()
                    .Count(e => e.TenantId == t.Id && !e.IsDeleted)
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new TenantDto(r.Id, r.Seq, r.Name, r.IsActive, r.CompanyCount, r.CreatedOn))
            .ToList();
    }
}
