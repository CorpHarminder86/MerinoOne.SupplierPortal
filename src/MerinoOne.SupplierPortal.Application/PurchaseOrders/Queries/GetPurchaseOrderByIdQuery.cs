using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

public record GetPurchaseOrderByIdQuery(Guid Id) : IRequest<PurchaseOrderDetailDto>;

public class GetPurchaseOrderByIdQueryHandler : IRequestHandler<GetPurchaseOrderByIdQuery, PurchaseOrderDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly IFulfilmentSettings _fulfilment;
    public GetPurchaseOrderByIdQueryHandler(IAppDbContext db, IFulfilmentSettings fulfilment)
    {
        _db = db;
        _fulfilment = fulfilment;
    }

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
        // R4 (2026-06-26) — Addendum §7.3 / DI-04: surface ShippedQtyToDate + the two DERIVED figures (Balance,
        // OverShipAllowance) on the PO-line-picker so the ASN wizard shows remaining-to-ship and the tolerance
        // headroom. The over-ship tolerance is two-tier (§7.1): SupplierItem(supplierId, itemId).OverShipTolerancePct
        // (when present AND non-null) overrides Item.OverShipTolerancePct. The SupplierItem override is left-joined on
        // the resolved item id (l.ItemId or the company+code-matched Item.Id). IgnoreQueryFilters mirrors the Item
        // join — SupplierItem is tenant/seccode-owned but this is a read-only projection.
        var poCompany = row.po.TenantEntityId;
        var poSupplierId = row.po.SupplierId;
        var lineRows = await (from l in _db.PurchaseOrderLines
                           where l.PurchaseOrderId == request.Id
                           join itm in _db.Items.IgnoreQueryFilters().Where(i => i.TenantEntityId == poCompany && !i.IsDeleted)
                               on l.ItemCode equals itm.Code into itemGroup
                           from item in itemGroup.DefaultIfEmpty()
                           join si in _db.SupplierItems.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.SupplierId == poSupplierId)
                               on (l.ItemId ?? (item != null ? (Guid?)item.Id : null)) equals (Guid?)si.ItemId into siGroup
                           from supItem in siGroup.DefaultIfEmpty()
                           orderby l.PositionNo
                           select new
                           {
                               Line = l,
                               ResolvedItemId = l.ItemId ?? (item != null ? (Guid?)item.Id : null),
                               IsSerialized = item != null && item.IsSerialized,
                               IsLotControlled = item != null && item.IsLotControlled,
                               ItemTolerance = item != null ? (decimal?)item.OverShipTolerancePct : null,
                               SupplierItemTolerance = supItem != null ? supItem.OverShipTolerancePct : null,
                           })
                          .ToListAsync(ct);

        var lines = lineRows.Select(r =>
        {
            var l = r.Line;
            // §7.1 — SupplierItem override (non-null) ?? Item floor ?? 0 (no resolvable item).
            var tolPct = r.SupplierItemTolerance ?? r.ItemTolerance ?? 0m;
            var balance = Math.Max(0m, l.OrderQty - l.ShippedQtyToDate);
            var overShipAllowance = Shipments.Policies.OverShipTolerance.RoundAllowance(
                Math.Max(0m, (l.OrderQty * Shipments.Policies.OverShipTolerance.Factor(tolPct)) - l.ShippedQtyToDate),
                _fulfilment.OverShipAllowanceRounding);
            // §5.3 / UC-ASN-10 — flag a line whose orderQty was revised below the already-shipped cumulative.
            var isOverShippedQtyReduced = l.ShippedQtyToDate > l.OrderQty;
            return new PurchaseOrderLineDto(
                l.Id, l.PositionNo, l.SequenceNo, l.ItemCode, l.ItemDescription,
                l.OrderUnit, l.OrderQty, l.PriceUnit, l.Price,
                l.DiscountPct, l.DiscountAmount, l.DeliveryDate, l.TaxCode,
                l.TaxDescription, l.TaxId,
                r.ResolvedItemId,
                r.IsSerialized,
                r.IsLotControlled,
                l.Price - l.DiscountAmount,
                l.ShippedQtyToDate, balance, overShipAllowance,
                isOverShippedQtyReduced);
        }).ToList();

        // PO header total = sum of line net amounts (Price − DiscountAmount). Derived, not persisted.
        var totalAmount = lines.Sum(l => l.NetAmount);

        return new PurchaseOrderDetailDto(
            row.po.Id, row.po.Seq, row.po.PoNumber,
            row.po.SupplierId, row.s.LegalName, row.s.SupplierCode,
            row.po.BuyerUserId,
            row.po.PoType.ToString(), row.po.PoDate,
            row.po.PaymentTerms, row.po.DeliveryTerms,
            row.po.CurrencyCode,
            row.po.PoStatus.ToString(),
            row.po.AcknowledgmentAt, row.po.AcceptedAt,
            row.po.RejectionReason,
            row.po.Version, null, row.po.ErpSyncId, row.po.Notes,
            lines,
            // PO-confirmation mode from the owning supplier (already joined above) — drives accept/reject UI gating.
            row.s.PoConfirmationMode.ToString(),
            totalAmount,
            // R4 (2026-06-26) — Phase 5b / D1, D2: the action toggles drive the PO-detail Reject / Negotiation gating.
            row.s.AllowNegotiate,
            row.s.AllowReject);
    }
}
