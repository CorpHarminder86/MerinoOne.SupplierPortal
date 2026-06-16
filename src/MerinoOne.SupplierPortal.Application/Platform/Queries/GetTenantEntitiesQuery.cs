using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Platform;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Platform.Queries;

/// <summary>Platform-Admin: list the companies (TenantEntities) for a given tenant (cross-tenant).</summary>
public record GetTenantEntitiesQuery(Guid TenantId) : IRequest<List<TenantEntityDto>>;

public class GetTenantEntitiesQueryHandler : IRequestHandler<GetTenantEntitiesQuery, List<TenantEntityDto>>
{
    private readonly IAppDbContext _db;
    public GetTenantEntitiesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<TenantEntityDto>> Handle(GetTenantEntitiesQuery request, CancellationToken ct)
    {
        // IgnoreQueryFilters: cross-tenant Platform Admin read. Re-apply !IsDeleted and restrict by the
        // requested tenant explicitly so nothing else leaks.
        var rows = await _db.TenantEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.TenantId == request.TenantId)
            .OrderBy(e => e.Code)
            .Select(e => new TenantEntityDto(
                e.Id, e.Seq, e.TenantId!.Value, e.Code, e.Name, e.IsActive, e.CreatedOn))
            .ToListAsync(ct);

        return rows;
    }
}
