using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

public record GetGoodsReceiptListQuery(
    int Page = 1,
    int PageSize = 50,
    Guid? PurchaseOrderId = null,
    Guid? AsnId = null,
    string? Search = null) : IRequest<PagedResult<GoodsReceiptDto>>;

public class GetGoodsReceiptListQueryHandler : IRequestHandler<GetGoodsReceiptListQuery, PagedResult<GoodsReceiptDto>>
{
    private readonly IAppDbContext _db;
    public GetGoodsReceiptListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<GoodsReceiptDto>> Handle(GetGoodsReceiptListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from g in _db.GoodsReceipts
                join pol in _db.PurchaseOrderLines on g.PurchaseOrderLineId equals pol.Id
                join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                join a in _db.Asns on g.AsnId equals (Guid?)a.Id into ag
                from a in ag.DefaultIfEmpty()
                select new { g, pol, po, a };

        if (request.PurchaseOrderId.HasValue)
            q = q.Where(x => x.po.Id == request.PurchaseOrderId.Value);
        if (request.AsnId.HasValue)
            q = q.Where(x => x.g.AsnId == request.AsnId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.g.GrnNumber.Contains(t) || x.po.PoNumber.Contains(t));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.g.GrnDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new GoodsReceiptDto(
                x.g.Id, x.g.Seq, x.g.GrnNumber,
                x.g.PurchaseOrderLineId, x.pol.PositionNo, x.po.PoNumber, x.pol.ItemCode,
                x.g.AsnId, x.a != null ? x.a.AsnNumber : null,
                x.g.ReceivedQty, x.g.ShortQty, x.g.RejectedQty,
                x.g.GrnDate, x.g.ErpSyncId))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<GoodsReceiptDto>(items, page, pageSize, total, totalPages);
    }
}
