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
    public GetSupplierChangeRequestListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<SupplierChangeRequestListItemDto>> Handle(GetSupplierChangeRequestListQuery request, CancellationToken ct)
    {
        var q = _db.SupplierChangeRequests.AsQueryable();

        if (request.SupplierId.HasValue)
            q = q.Where(r => r.SupplierId == request.SupplierId.Value);
        if (!string.IsNullOrEmpty(request.Status))
            q = q.Where(r => r.ChangeStatus.ToString() == request.Status);

        // Join Supplier for the display code/name (Supplier is also seccode-scoped — same supplier visibility).
        return await (
            from r in q
            join s in _db.Suppliers on r.SupplierId equals s.Id
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
