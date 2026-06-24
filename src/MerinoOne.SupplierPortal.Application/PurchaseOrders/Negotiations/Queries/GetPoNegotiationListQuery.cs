using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Queries;

/// <summary>
/// Lists PO negotiations (supplier's own / buyer review queue). Mirrors <c>GetSupplierChangeRequestListQuery</c>:
/// a reviewer's queue spans ALL suppliers/companies in the tenant — the seccode + active-company filters would
/// otherwise show a buyer an empty queue (they hold no SecRight on supplier G-seccodes, and the negotiations live
/// under the supplier's company, not the reviewer's). Bypass the filters but re-apply the tenant predicate so it
/// never crosses tenants. Suppliers stay seccode-scoped to their own. Optional status filter.
/// </summary>
public record GetPoNegotiationListQuery(string? Status = null) : IRequest<List<PoNegotiationListItemDto>>;

public class GetPoNegotiationListQueryHandler
    : IRequestHandler<GetPoNegotiationListQuery, List<PoNegotiationListItemDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetPoNegotiationListQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<PoNegotiationListItemDto>> Handle(GetPoNegotiationListQuery request, CancellationToken ct)
    {
        var reviewer = _user.IsAdmin || _user.IsManager || _user.HasPermission("PurchaseOrder.ApproveNegotiation");
        var q = reviewer
            ? _db.PurchaseOrderNegotiations.IgnoreQueryFilters().Where(n => !n.IsDeleted && n.TenantId == _user.TenantId)
            : _db.PurchaseOrderNegotiations.AsQueryable();
        var sups = reviewer
            ? _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantId == _user.TenantId)
            : _db.Suppliers.AsQueryable();

        if (!string.IsNullOrEmpty(request.Status))
            q = q.Where(n => n.NegotiationStatus.ToString() == request.Status);

        return await (
            from n in q
            join s in sups on n.SupplierId equals s.Id
            orderby n.SubmittedAt descending
            select new PoNegotiationListItemDto(
                n.Id,
                n.PoNumber,
                s.LegalName,
                n.NegotiationStatus.ToString(),
                n.Lines.Count(l => !l.IsDeleted),
                n.SubmittedAt))
            .ToListAsync(ct);
    }
}
