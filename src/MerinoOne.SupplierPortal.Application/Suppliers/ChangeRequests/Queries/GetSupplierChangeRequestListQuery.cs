using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Queries;

/// <summary>
/// Lists change requests. Filterable by supplier + status. Seccode-scoped automatically: because
/// <c>SupplierChangeRequest</c> is a <c>BaseAggregateRoot</c> (ISeccode), the always-on seccode filter restricts a
/// supplier principal to its OWN requests, while internal/privileged users (Admin/Manager — broad SecRights) see
/// all. No special branching is needed: the RLS engine does the supplier-vs-internal split.
/// </summary>
public record GetSupplierChangeRequestListQuery(Guid? SupplierId = null, string? Status = null)
    : IRequest<List<SupplierChangeRequestListItemDto>>;

public class GetSupplierChangeRequestListQueryHandler
    : IRequestHandler<GetSupplierChangeRequestListQuery, List<SupplierChangeRequestListItemDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetSupplierChangeRequestListQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<SupplierChangeRequestListItemDto>> Handle(GetSupplierChangeRequestListQuery request, CancellationToken ct)
    {
        // A reviewer's queue spans ALL suppliers/companies in the tenant — the seccode + active-company filters
        // would otherwise show an admin an empty queue (they hold no SecRight on supplier G-seccodes, and the
        // requests live under the supplier's company, not the reviewer's active one). Bypass the filters but
        // re-apply the tenant predicate so it never crosses tenants. Suppliers stay seccode-scoped to their own.
        var reviewer = _user.IsAdmin || _user.IsManager || _user.HasPermission("Supplier.ApproveChange");
        var q = reviewer
            ? _db.SupplierChangeRequests.IgnoreQueryFilters().Where(r => !r.IsDeleted && r.TenantId == _user.TenantId)
            : _db.SupplierChangeRequests.AsQueryable();
        var sups = reviewer
            ? _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantId == _user.TenantId)
            : _db.Suppliers.AsQueryable();

        if (request.SupplierId.HasValue)
            q = q.Where(r => r.SupplierId == request.SupplierId.Value);
        if (!string.IsNullOrEmpty(request.Status))
            q = q.Where(r => r.ChangeStatus.ToString() == request.Status);

        // Join Supplier for the display code/name.
        return await (
            from r in q
            join s in sups on r.SupplierId equals s.Id
            orderby r.RequestedAt descending
            select new SupplierChangeRequestListItemDto(
                r.Id,
                r.Seq,
                r.SupplierId,
                s.SupplierCode,
                s.LegalName,
                r.ChangeStatus.ToString(),
                r.Summary,
                r.RequestedBy,
                r.RequestedAt,
                r.ReviewedBy,
                r.ReviewedAt,
                r.Lines.Count(l => !l.IsDeleted),
                r.CreatedOn))
            .ToListAsync(ct);
    }
}
