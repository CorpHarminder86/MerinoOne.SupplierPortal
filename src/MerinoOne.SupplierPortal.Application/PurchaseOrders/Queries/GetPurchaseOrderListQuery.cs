using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

public record GetPurchaseOrderListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    string? Type = null,
    Guid? SupplierId = null,
    string? Search = null) : IRequest<PagedResult<PurchaseOrderListItemDto>>;

public class GetPurchaseOrderListQueryHandler : IRequestHandler<GetPurchaseOrderListQuery, PagedResult<PurchaseOrderListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetPurchaseOrderListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<PurchaseOrderListItemDto>> Handle(GetPurchaseOrderListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from po in _db.PurchaseOrders
                join s in _db.Suppliers on po.SupplierId equals s.Id
                select new { po, s };

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            // Accept either a single value ("Released") or a comma-separated list ("Released,Acknowledged,Accepted").
            // Backend evaluates as WHERE PoStatus IN (...) so the frontend issues one call instead of N.
            var statusValues = request.Status
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (statusValues.Count == 1)
            {
                var single = statusValues[0];
                q = q.Where(x => x.po.PoStatus.ToString() == single);
            }
            else if (statusValues.Count > 1)
            {
                q = q.Where(x => statusValues.Contains(x.po.PoStatus.ToString()));
            }
        }
        if (!string.IsNullOrWhiteSpace(request.Type))
            q = q.Where(x => x.po.PoType.ToString() == request.Type);
        if (request.SupplierId.HasValue)
            q = q.Where(x => x.po.SupplierId == request.SupplierId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.po.PoNumber.Contains(t) || x.s.LegalName.Contains(t) || x.s.SupplierCode.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.po.PoDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new PurchaseOrderListItemDto(
                x.po.Id, x.po.Seq, x.po.PoNumber,
                x.po.SupplierId, x.s.LegalName, x.s.SupplierCode,
                x.po.PoType.ToString(), x.po.PoDate, x.po.PoStatus.ToString(),
                x.po.Version, x.po.CreatedOn,
                // PO-confirmation mode from the joined supplier — per-row accept/reject gating, no N+1.
                x.s.PoConfirmationMode.ToString(),
                // Total = sum of live line net amounts (Price − DiscountAmount); correlated subquery, paged so bounded.
                _db.PurchaseOrderLines.Where(l => !l.IsDeleted && l.PurchaseOrderId == x.po.Id)
                    .Sum(l => (decimal?)(l.Price - l.DiscountAmount)) ?? 0m,
                // Term display: the header snapshot string (written at inbound from the term master — no read join).
                x.po.CurrencyCode, x.po.PaymentTerms, x.po.DeliveryTerms,
                // Ship-to (from the owned snapshot) — the ASN wizard groups/filters open POs by ship-to.
                x.po.ShipToAddressId, x.po.ShipTo != null ? x.po.ShipTo.AddressName : null))
            .ToListAsync(ct);

        // R4 §6.2 — stamp the ship-gate result per row (the policy is not translatable to SQL and the Web layer
        // cannot call it, so compute it here from the already-projected PoStatus + supplier mode).
        items = items.Select(d => d with
        {
            IsShippable = Enum.TryParse<PoStatus>(d.PoStatus, out var st)
                          && Enum.TryParse<PoConfirmationMode>(d.PoResponseMode, out var md)
                          && PoConfirmationPolicy.AllowsShipping(st, md)
        }).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<PurchaseOrderListItemDto>(items, page, pageSize, total, totalPages);
    }
}
