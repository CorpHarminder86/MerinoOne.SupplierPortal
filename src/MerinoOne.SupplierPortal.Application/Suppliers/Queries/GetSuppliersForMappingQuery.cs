using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Queries;

/// <summary>
/// Lists a company's suppliers for the admin user-mapping picker. Admin mapping is tenant-wide config, so this
/// must NOT be scoped to the header's active company (the admin may be sitting on company 3000 while mapping a
/// user under 2000). It therefore <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/> to drop the
/// always-on company + seccode filters, then re-applies explicit, safe scoping: not-deleted + the requested
/// company + the acting tenant (so a different tenant's suppliers can never leak in). Gated by Supplier.Provision.
/// </summary>
public record GetSuppliersForMappingQuery(Guid TenantEntityId, string? Search = null)
    : IRequest<List<SupplierListItemDto>>;

public class GetSuppliersForMappingQueryHandler : IRequestHandler<GetSuppliersForMappingQuery, List<SupplierListItemDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetSuppliersForMappingQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<List<SupplierListItemDto>> Handle(GetSuppliersForMappingQuery request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        var q = _db.Suppliers.IgnoreQueryFilters()
            .Where(s => !s.IsDeleted && s.TenantEntityId == request.TenantEntityId);

        // Tenant boundary is the #1 security property — never expose another tenant's suppliers even though the
        // company filter is bypassed. (Null tenant only for the platform-admin/system principal, which won't hit this.)
        if (tenantId.HasValue)
            q = q.Where(s => s.TenantId == tenantId.Value);

        if (!string.IsNullOrEmpty(request.Search))
            q = q.Where(s => s.LegalName.Contains(request.Search) || s.SupplierCode.Contains(request.Search));

        return await q
            .OrderBy(s => s.LegalName)
            .Select(s => new SupplierListItemDto(
                s.Id, s.Seq, s.SupplierCode, s.LegalName, s.TradeName,
                s.GstNumber, s.PanNumber, s.RegistrationStatus.ToString(),
                s.IsActiveSupplier, s.CreatedOn))
            .ToListAsync(ct);
    }
}
