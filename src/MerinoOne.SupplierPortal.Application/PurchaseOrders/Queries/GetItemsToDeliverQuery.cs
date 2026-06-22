using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

/// <summary>
/// Enhancement R4 — Module 8 (Items-to-be-Delivered). Open PO lines grouped by (ItemCode, DeliveryDate),
/// netted against received GRN qty. EF path only — the global seccode/company filter scopes a supplier's
/// own PO lines (no Dapper leak path, plan §3 Module 8).
/// <para>OpenQty = OrderQty − Σ GoodsReceipt.ReceivedQty (GRN keyed per PO line).</para>
/// <para>Open-PO filter: PoStatus ∈ {Released, Acknowledged, Accepted, DateProposed, PartiallyDelivered}.</para>
/// </summary>
public record GetItemsToDeliverQuery(
    int Page = 1,
    int PageSize = 50,
    DateTime? From = null,
    DateTime? To = null,
    string? ItemCode = null,
    Guid? SupplierId = null) : IRequest<PagedResult<ItemsToDeliverRowDto>>;

public class GetItemsToDeliverQueryHandler : IRequestHandler<GetItemsToDeliverQuery, PagedResult<ItemsToDeliverRowDto>>
{
    // Open-PO statuses (plan completeness #26 — DateProposed included).
    private static readonly PoStatus[] OpenStatuses =
    {
        PoStatus.Released,
        PoStatus.Acknowledged,
        PoStatus.Accepted,
        PoStatus.DateProposed,
        PoStatus.PartiallyDelivered,
    };

    private readonly IAppDbContext _db;
    public GetItemsToDeliverQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<ItemsToDeliverRowDto>> Handle(GetItemsToDeliverQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        // PO lines on open POs only. PurchaseOrders is seccode-scoped, so a supplier only ever sees its own
        // open lines (the join inherits the filtered set).
        // EF can't translate `OpenStatuses.Contains(enumProperty)` for a string-converted enum, so expand to
        // an explicit OR-chain (same idiom as GetDashboardSummaryQuery).
        var lines = from l in _db.PurchaseOrderLines
                    join po in _db.PurchaseOrders on l.PurchaseOrderId equals po.Id
                    where po.PoStatus == PoStatus.Released
                          || po.PoStatus == PoStatus.Acknowledged
                          || po.PoStatus == PoStatus.Accepted
                          || po.PoStatus == PoStatus.DateProposed
                          || po.PoStatus == PoStatus.PartiallyDelivered
                    select new { l, po };

        if (request.SupplierId.HasValue)
            lines = lines.Where(x => x.po.SupplierId == request.SupplierId.Value);
        if (request.From.HasValue)
            lines = lines.Where(x => x.l.DeliveryDate >= request.From.Value);
        if (request.To.HasValue)
            lines = lines.Where(x => x.l.DeliveryDate <= request.To.Value);
        if (!string.IsNullOrWhiteSpace(request.ItemCode))
        {
            var code = request.ItemCode.Trim();
            lines = lines.Where(x => x.l.ItemCode == code);
        }

        // Per-line received qty (Σ GoodsReceipt.ReceivedQty keyed per PO line). GoodsReceipts is seccode-scoped.
        // Compute the per-line open qty, then GroupBy (ItemCode, DeliveryDate) server-side.
        var grouped = lines
            .Select(x => new
            {
                x.l.ItemCode,
                x.l.ItemDescription,
                x.l.DeliveryDate,
                x.po.Id,
                x.l.OrderQty,
                ReceivedQty = _db.GoodsReceipts.Where(g => g.PurchaseOrderLineId == x.l.Id).Sum(g => (decimal?)g.ReceivedQty) ?? 0m
            })
            .GroupBy(x => new { x.ItemCode, x.DeliveryDate })
            .Select(g => new
            {
                g.Key.ItemCode,
                g.Key.DeliveryDate,
                ItemName = g.Max(x => x.ItemDescription),
                TotalQty = g.Sum(x => x.OrderQty),
                OpenQty = g.Sum(x => x.OrderQty - x.ReceivedQty),
                PoCount = g.Select(x => x.Id).Distinct().Count()
            });

        var total = await grouped.CountAsync(ct);

        var rows = await grouped
            .OrderBy(g => g.DeliveryDate).ThenBy(g => g.ItemCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new ItemsToDeliverRowDto(
                g.ItemCode,
                g.ItemName,
                g.DeliveryDate,
                g.TotalQty,
                g.OpenQty,
                g.PoCount))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<ItemsToDeliverRowDto>(rows, page, pageSize, total, totalPages);
    }
}
