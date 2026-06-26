using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// Entity → DTO projection for the PO-negotiation detail + lines, shared by the create command (which returns
/// the freshly-built aggregate) and the by-id query. Lines are projected from the parent's <c>Lines</c>
/// collection (there is intentionally no root DbSet for lines). Soft-deleted lines are excluded. Mirrors
/// <c>SupplierChangeRequestMapper</c>.
/// </summary>
public static class PoNegotiationMapper
{
    public static PoNegotiationDto ToDto(PurchaseOrderNegotiation n, string supplierName)
        => new(
            n.Id,
            n.Seq,
            n.PurchaseOrderId,
            n.PoNumber,
            n.SupplierId,
            supplierName,
            n.NegotiationStatus.ToString(),
            n.PreviousPoStatus.ToString(),
            n.Notes,
            n.RejectionReason,
            n.SubmittedAt,
            n.ReviewedAt,
            n.ReviewedBy,
            n.CreatedOn,
            n.Lines
                .Where(l => !l.IsDeleted)
                .OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo)
                .Select(ToLineDto)
                .ToList());

    public static PoNegotiationLineDto ToLineDto(PurchaseOrderNegotiationLine l)
        => new(
            l.PurchaseOrderLineId,
            l.PositionNo,
            l.SequenceNo,
            l.ItemCode,
            l.OriginalQty,
            l.NegotiatedQty,
            l.OriginalDeliveryDate,
            l.NegotiatedDeliveryDate,
            l.OriginalPrice,
            l.NegotiatedPrice);
}
