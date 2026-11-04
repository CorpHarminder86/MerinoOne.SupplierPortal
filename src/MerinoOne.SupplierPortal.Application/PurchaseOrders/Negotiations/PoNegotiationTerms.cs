using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// R18 (2026-11-03) — the three values a negotiation can move on one PO line: quantity, delivery instant and unit price.
/// Value equality is numeric / by instant (decimal scale and DateTime kind do not matter), which is exactly the "did this
/// line change" test the negotiation rules apply everywhere.
/// </summary>
internal readonly record struct PoLineTerms(decimal Qty, DateTime? DeliveryDate, decimal Price)
{
    /// <summary>What the live PO line currently says.</summary>
    public static PoLineTerms Of(PurchaseOrderLine line) => new(line.OrderQty, line.DeliveryDate, line.PriceUnit);

    /// <summary>What a stored negotiation line proposes.</summary>
    public static PoLineTerms Of(PurchaseOrderNegotiationLine line)
        => new(line.NegotiatedQty, line.NegotiatedDeliveryDate, line.NegotiatedPrice);
}

internal static class PoNegotiationTerms
{
    /// <summary>
    /// A round's EFFECTIVE terms per PO line: the live PO lines' own values with the round's stored lines overlaid. A round
    /// stores deltas only — lines whose proposal differs from the live PO line — so a line it does not store stands for the
    /// live value. Because every round (a supplier's opening proposal and each counter alike) stores its deltas against the
    /// LIVE PO line, the effective terms are the whole proposal on their own; the chain's earlier rounds never need replaying.
    /// A duplicate stored line for one PO line resolves last-wins, as the LN payload builder resolves it.
    /// </summary>
    public static Dictionary<Guid, PoLineTerms> Effective(
        IEnumerable<(Guid LineId, PoLineTerms Terms)> liveLines, IEnumerable<PurchaseOrderNegotiationLine> roundLines)
    {
        var effective = new Dictionary<Guid, PoLineTerms>();
        foreach (var (lineId, terms) in liveLines) effective[lineId] = terms;
        foreach (var stored in roundLines.Where(l => !l.IsDeleted)) effective[stored.PurchaseOrderLineId] = PoLineTerms.Of(stored);
        return effective;
    }
}
