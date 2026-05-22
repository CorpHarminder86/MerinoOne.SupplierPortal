using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

public record GetDeliveryScheduleListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? PurchaseOrderId = null) : IRequest<PagedResult<DeliveryScheduleDto>>;

public class GetDeliveryScheduleListQueryHandler : IRequestHandler<GetDeliveryScheduleListQuery, PagedResult<DeliveryScheduleDto>>
{
    private readonly IAppDbContext _db;
    public GetDeliveryScheduleListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<DeliveryScheduleDto>> Handle(GetDeliveryScheduleListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from ds in _db.DeliverySchedules
                join po in _db.PurchaseOrders on ds.PurchaseOrderId equals po.Id
                select new { ds, po };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.ds.ScheduleStatus.ToString() == request.Status);
        if (request.PurchaseOrderId.HasValue)
            q = q.Where(x => x.ds.PurchaseOrderId == request.PurchaseOrderId.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.ds.ProposedDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new DeliveryScheduleDto(
                x.ds.Id, x.ds.Seq, x.ds.PurchaseOrderId, x.po.PoNumber,
                x.ds.ProposedDate, x.ds.TimeWindow, x.ds.VehicleInfo,
                x.ds.ScheduleStatus.ToString(), x.ds.ApprovedBy, x.ds.RejectionReason,
                x.ds.CreatedOn))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<DeliveryScheduleDto>(items, page, pageSize, total, totalPages);
    }
}
