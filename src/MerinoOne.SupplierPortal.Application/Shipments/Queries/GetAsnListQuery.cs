using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

public record GetAsnListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? SupplierId = null,
    Guid? PurchaseOrderId = null,
    string? Search = null) : IRequest<PagedResult<AsnListItemDto>>;

public class GetAsnListQueryHandler : IRequestHandler<GetAsnListQuery, PagedResult<AsnListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetAsnListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<AsnListItemDto>> Handle(GetAsnListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from a in _db.Asns
                join po in _db.PurchaseOrders on a.PurchaseOrderId equals po.Id
                join s in _db.Suppliers on a.SupplierId equals s.Id
                select new { a, po, s };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.a.AsnStatus.ToString() == request.Status);
        if (request.SupplierId.HasValue)
            q = q.Where(x => x.a.SupplierId == request.SupplierId.Value);
        if (request.PurchaseOrderId.HasValue)
            q = q.Where(x => x.a.PurchaseOrderId == request.PurchaseOrderId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.a.AsnNumber.Contains(t) || x.po.PoNumber.Contains(t) || x.s.LegalName.Contains(t));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.a.ExpectedDeliveryDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AsnListItemDto(
                x.a.Id, x.a.Seq, x.a.AsnNumber, x.a.PurchaseOrderId, x.po.PoNumber,
                x.a.SupplierId, x.s.LegalName,
                x.a.ExpectedDeliveryDate, x.a.CarrierName, x.a.TrackingNumber,
                x.a.AsnStatus.ToString(), x.a.CreatedOn))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<AsnListItemDto>(items, page, pageSize, total, totalPages);
    }
}
