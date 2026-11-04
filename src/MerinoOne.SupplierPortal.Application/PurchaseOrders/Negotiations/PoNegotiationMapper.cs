using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// Entity → DTO projection for the PO-negotiation detail + lines, shared by the create command (which returns
/// the freshly-built aggregate) and the by-id query. Lines are projected from the parent's <c>Lines</c>
/// collection (there is intentionally no root DbSet for lines). Soft-deleted lines are excluded. Mirrors
/// <c>SupplierChangeRequestMapper</c>.
///
/// <para>R18 (2026-11-03): the DTO also names the round (RoundNo / ProposedBy / ParentNegotiationId). The round passed in is the
/// one an action just produced or resolved, which is the head of its chain, so <c>HeadNegotiationId</c> is its own id; the
/// by-id query, which maps any round of a chain, overrides it with the real head and adds the chain's <c>Rounds</c>.</para>
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
                .ToList(),
            RoundNo: n.RoundNo,
            ProposedBy: n.ProposedBy.ToString(),
            ParentNegotiationId: n.ParentNegotiationId,
            HeadNegotiationId: n.Id);

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
