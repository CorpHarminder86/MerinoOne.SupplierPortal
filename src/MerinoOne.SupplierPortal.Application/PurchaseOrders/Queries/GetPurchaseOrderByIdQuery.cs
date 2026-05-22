using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

public record GetPurchaseOrderByIdQuery(Guid Id) : IRequest<PurchaseOrderDetailDto>;

public class GetPurchaseOrderByIdQueryHandler : IRequestHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDetailDto>
{
    private readonly IAppDbContext _db;
    public GetPurchaseOrderByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PurchaseOrderDetailDto> Handle(GetPurchaseOrderByIdQuery request, CancellationToken ct)
    {
        var row = await (from po in _db.PurchaseOrders
                         join s in _db.Suppliers on po.SupplierId equals s.Id
                         where po.Id == request.Id
                         select new { po, s })
                        .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("PurchaseOrder", request.Id);

        var lines = await _db.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == request.Id)
            .OrderBy(l => l.PositionNo)
            .Select(l => new PurchaseOrderLineDto(
                l.Id, l.PositionNo, l.SequenceNo, l.ItemCode, l.ItemDescription,
                l.OrderUnit, l.OrderQty, l.PriceUnit, l.Price,
                l.DiscountPct, l.DiscountAmount, l.DeliveryDate, l.TaxCode))
            .ToListAsync(ct);

        return new PurchaseOrderDetailDto(
            row.po.Id, row.po.Seq, row.po.PoNumber,
            row.po.SupplierId, row.s.LegalName, row.s.SupplierCode,
            row.po.BuyerUserId,
            row.po.PoType.ToString(), row.po.PoDate,
            row.po.PaymentTerms, row.po.DeliveryTerms,
            row.po.PoStatus.ToString(),
            row.po.AcknowledgmentAt, row.po.AcceptedAt,
            row.po.RejectionReason, row.po.ProposedDeliveryDate,
            row.po.Version, null, row.po.ErpSyncId, row.po.Notes,
            lines);
    }
}
