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

        // R4 (2026-06-23) — Serial/Lot capture: resolve inv.Item by **ItemCode within the PO's company** to surface
        // the control flags the ASN wizard reads. PO lines are ERP-fed and frequently carry a NULL ItemId (the
        // inbound upsert only backfills ItemId when the item already existed at push time — a late item push, or a
        // bulk-seeded PO, leaves it null), but ItemCode is always present. Item's natural key is (TenantEntityId,
        // Code), so scoping the code match to the PO's TenantEntityId keeps it unambiguous and reader-agnostic
        // (IgnoreQueryFilters so an internal viewer on a different active company still resolves the flags). Flags
        // default false when no matching Item exists.
        var poCompany = row.po.TenantEntityId;
        var lines = await (from l in _db.PurchaseOrderLines
                           where l.PurchaseOrderId == request.Id
                           join itm in _db.Items.IgnoreQueryFilters().Where(i => i.TenantEntityId == poCompany && !i.IsDeleted)
                               on l.ItemCode equals itm.Code into itemGroup
                           from item in itemGroup.DefaultIfEmpty()
                           orderby l.PositionNo
                           select new PurchaseOrderLineDto(
                               l.Id, l.PositionNo, l.SequenceNo, l.ItemCode, l.ItemDescription,
                               l.OrderUnit, l.OrderQty, l.PriceUnit, l.Price,
                               l.DiscountPct, l.DiscountAmount, l.DeliveryDate, l.TaxCode,
                               l.TaxDescription, l.TaxId,
                               l.ItemId ?? (item != null ? (Guid?)item.Id : null),
                               item != null && item.IsSerialized,
                               item != null && item.IsLotControlled))
                          .ToListAsync(ct);

        return new PurchaseOrderDetailDto(
            row.po.Id, row.po.Seq, row.po.PoNumber,
            row.po.SupplierId, row.s.LegalName, row.s.SupplierCode,
            row.po.BuyerUserId,
            row.po.PoType.ToString(), row.po.PoDate,
            row.po.PaymentTerms, row.po.DeliveryTerms,
            row.po.CurrencyCode,
            row.po.PoStatus.ToString(),
            row.po.AcknowledgmentAt, row.po.AcceptedAt,
            row.po.RejectionReason, row.po.ProposedDeliveryDate,
            row.po.Version, null, row.po.ErpSyncId, row.po.Notes,
            lines,
            // PO-response mode from the owning supplier (already joined above) — drives accept/reject UI gating.
            row.s.PoResponseMode.ToString());
    }
}
