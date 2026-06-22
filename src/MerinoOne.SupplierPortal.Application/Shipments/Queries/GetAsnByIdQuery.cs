using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

public record GetAsnByIdQuery(Guid Id) : IRequest<AsnDetailDto>;

public class GetAsnByIdQueryHandler : IRequestHandler<GetAsnByIdQuery, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    public GetAsnByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<AsnDetailDto> Handle(GetAsnByIdQuery request, CancellationToken ct)
    {
        var row = await (from a in _db.Asns
                         join po in _db.PurchaseOrders on a.PurchaseOrderId equals po.Id
                         join s in _db.Suppliers on a.SupplierId equals s.Id
                         where a.Id == request.Id
                         select new { a, po, s })
                        .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Asn", request.Id);

        var lines = await (from al in _db.AsnLines
                           join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                           where al.AsnId == request.Id
                           orderby pol.PositionNo
                           select new AsnLineDto(
                               al.Id, al.PurchaseOrderLineId, pol.PositionNo,
                               pol.ItemCode, pol.ItemDescription, pol.OrderUnit, pol.OrderQty,
                               al.ShippedQty, al.BatchNumber, al.ExpiryDate))
                          .ToListAsync(ct);

        return new AsnDetailDto(
            row.a.Id, row.a.Seq, row.a.AsnNumber,
            // R4 0019: PurchaseOrderId is now nullable (multi-PO). Compile shim only — existing single-PO
            // ASNs always have a value; backend reshapes the DTO for multi-PO in Increment B.
            row.a.PurchaseOrderId ?? Guid.Empty, row.po.PoNumber,
            row.a.SupplierId, row.s.LegalName,
            row.a.ExpectedDeliveryDate, row.a.TimeWindow,
            row.a.CarrierName, row.a.TrackingNumber,
            row.a.VehicleNumber, row.a.DriverName, row.a.DriverPhone,
            row.a.AsnStatus.ToString(), row.a.Notes,
            lines);
    }
}
