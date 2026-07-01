using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §6.3 / §6.4, Component 2 (Live PO-Qty Linkage & PO-Change Sync). Pure,
/// trivially unit-testable diff that decides whether an inbound PO re-push (an ERP <c>Modify</c>) is MATERIAL —
/// i.e. it must re-arm the supplier confirmation gate (reset <c>PoStatus → Released</c>, §6.3) — or NON-MATERIAL
/// (notes / internal ref only), which bumps <c>Version</c> without freezing the supplier mid-fulfilment (UC-PO-07).
///
/// <para><b>Material fields (§6.4):</b> order qty, price (unit price OR extended line amount), delivery date, and
/// line ADD / REMOVE. Anything else (notes, item description, tax code, discount) is non-material for the purpose
/// of re-confirmation. The diff matches incoming ERP lines to persisted lines on the storage key <c>PositionNo</c>
/// (seq is always stored as 1; multiple inbound seqs per position are folded). The qty comparison uses the
/// RESOLVED target qty (replace / signed-add / no-op) — NOT the raw incoming orderQty, which is 0 on an add and
/// would otherwise read as a qty→0 drop (§5.2 / R4 2026-06-30).</para>
/// </summary>
public static class PoMaterialChange
{
    /// <summary>
    /// True when the incoming push materially changes the PO vs what is persisted: any position's RESOLVED order
    /// qty, unit price, extended price, or delivery date changed, OR a position was added / removed.
    /// <paramref name="existingLines"/> are the persisted (non-deleted) lines; <paramref name="incomingLines"/>
    /// are the ERP push's lines; <paramref name="resolvedByPosition"/> is the resolved absolute target qty per
    /// position (from the inbound upsert's ResolvePoLineQuantities).
    /// </summary>
    public static bool IsMaterial(
        IReadOnlyCollection<PurchaseOrderLine> existingLines,
        IReadOnlyCollection<PoLineRecord> incomingLines,
        IReadOnlyDictionary<int, ResolvedPoLine> resolvedByPosition)
    {
        var existingByPos = new Dictionary<int, PurchaseOrderLine>();
        foreach (var l in existingLines)
            existingByPos[l.PositionNo] = l;   // last-wins (defensive against any legacy dup)

        // Fold incoming by positionNo — the last line per position carries the material price/delivery attributes.
        var incomingByPos = new Dictionary<int, PoLineRecord>();
        foreach (var l in incomingLines)
            incomingByPos[l.PositionNo] = l;

        // Line ADDED — a pushed position with no persisted match (material: a new line changes the order).
        foreach (var pos in incomingByPos.Keys)
            if (!existingByPos.ContainsKey(pos))
                return true;

        // Line REMOVED — a persisted position absent from the push (material: the order shrank). Soft-handled by the
        // upsert, but still a material change for re-confirmation purposes (§6.4).
        foreach (var pos in existingByPos.Keys)
            if (!incomingByPos.ContainsKey(pos))
                return true;

        // Shared positions — compare the RESOLVED target qty + extended price against the persisted line. Both qty
        // and Price are folded (replace / signed-add / no-op), so an additive delta compares the resolved absolute
        // (old + delta), never the raw incoming delta. Unit price + delivery date come straight from the push.
        foreach (var (pos, incoming) in incomingByPos)
        {
            var existing = existingByPos[pos];
            var resolved = resolvedByPosition.TryGetValue(pos, out var rl)
                ? rl : new ResolvedPoLine(existing.OrderQty, existing.Price, existing.DiscountAmount);
            if (existing.OrderQty != resolved.Qty) return true;
            if (existing.PriceUnit != incoming.PriceUnit) return true;
            if (existing.Price != resolved.Price) return true;
            if (existing.DeliveryDate != incoming.DeliveryDate) return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a short, human-readable diff of the MATERIAL line changes for the supplier notification (§14):
    /// e.g. <c>"Line 10 qty 100→120 (60 remaining); Line 20 added; Line 30 removed"</c>. Computed AFTER the
    /// upsert applied the new values, so it reads the BEFORE snapshot (qty + price + date captured pre-update)
    /// against the incoming push. The revised ship balance (<c>MAX(0, newOrderQty − shippedQtyToDate)</c>) is
    /// surfaced per changed line so the supplier sees how much is left to ship.
    /// </summary>
    public static string DescribeDiff(
        IReadOnlyCollection<PoLineChangeSnapshot> beforeLines,
        IReadOnlyCollection<PoLineRecord> incomingLines,
        IReadOnlyDictionary<int, ResolvedPoLine> resolvedByPosition)
    {
        var beforeByPos = new Dictionary<int, PoLineChangeSnapshot>();
        foreach (var l in beforeLines)
            beforeByPos[l.PositionNo] = l;

        var incomingByPos = new Dictionary<int, PoLineRecord>();
        foreach (var l in incomingLines)
            incomingByPos[l.PositionNo] = l;

        var parts = new List<string>();

        foreach (var (pos, incoming) in incomingByPos)
        {
            var resolved = resolvedByPosition.TryGetValue(pos, out var rl)
                ? rl : new ResolvedPoLine(incoming.OrderQty, incoming.Price, incoming.DiscountAmount);
            if (!beforeByPos.TryGetValue(pos, out var before))
            {
                parts.Add($"Line {pos} added (qty {Fmt(resolved.Qty)})");
                continue;
            }

            var changes = new List<string>();
            if (before.OrderQty != resolved.Qty)
            {
                var remaining = Math.Max(0m, resolved.Qty - before.ShippedQtyToDate);
                changes.Add($"qty {Fmt(before.OrderQty)}→{Fmt(resolved.Qty)} ({Fmt(remaining)} remaining)");
            }
            if (before.PriceUnit != incoming.PriceUnit)
                changes.Add($"unit price {Fmt(before.PriceUnit)}→{Fmt(incoming.PriceUnit)}");
            if (before.Price != resolved.Price)
                changes.Add($"price {Fmt(before.Price)}→{Fmt(resolved.Price)}");
            if (before.DeliveryDate != incoming.DeliveryDate)
                changes.Add($"delivery {FmtDate(before.DeliveryDate)}→{FmtDate(incoming.DeliveryDate)}");

            if (changes.Count > 0)
                parts.Add($"Line {pos} {string.Join(", ", changes)}");
        }

        foreach (var pos in beforeByPos.Keys)
            if (!incomingByPos.ContainsKey(pos))
                parts.Add($"Line {pos} removed");

        return parts.Count == 0 ? "PO revised." : string.Join("; ", parts);
    }

    private static string Fmt(decimal d) => d == Math.Truncate(d) ? ((long)d).ToString() : d.ToString("0.####");
    private static string FmtDate(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "—";
}

/// <summary>
/// R4 (2026-06-26) — §6.4. A pre-update snapshot of a persisted PO line, captured BEFORE the inbound upsert
/// overwrites the line, so <see cref="PoMaterialChange.DescribeDiff"/> can render "100→120" and the revised ship
/// balance from the SAME cumulative (<see cref="ShippedQtyToDate"/> is preserved across the revision, §5.2).
/// </summary>
public readonly record struct PoLineChangeSnapshot(
    int PositionNo,
    int SequenceNo,
    decimal OrderQty,
    decimal PriceUnit,
    decimal Price,
    DateTime? DeliveryDate,
    decimal ShippedQtyToDate);

/// <summary>
/// R4 (2026-07-01) — the resolved absolute values for one PO position after folding the inbound push against the
/// stored line: <see cref="Qty"/>, extended <see cref="Price"/> and <see cref="Discount"/> (DiscountAmount) are each
/// REPLACED on an <c>orderQty&gt;0</c> line and ADDED on an <c>additionalQty≠0</c> delta line (0/0 = no-op).
/// </summary>
public readonly record struct ResolvedPoLine(decimal Qty, decimal Price, decimal Discount);
