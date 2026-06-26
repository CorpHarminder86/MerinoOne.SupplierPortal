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
/// of re-confirmation. The diff matches incoming ERP lines to persisted lines on the natural key
/// <c>(PositionNo, SequenceNo)</c> — the same key the in-place upsert keys on (§5.2).</para>
/// </summary>
public static class PoMaterialChange
{
    /// <summary>
    /// True when the incoming push materially changes the PO vs what is persisted: any line's order qty, unit
    /// price, extended price, or delivery date changed, OR a line was added / removed (by natural key).
    /// <paramref name="existingLines"/> are the persisted (non-deleted) lines; <paramref name="incomingLines"/>
    /// are the ERP push's lines.
    /// </summary>
    public static bool IsMaterial(
        IReadOnlyCollection<PurchaseOrderLine> existingLines,
        IReadOnlyCollection<PoLineRecord> incomingLines)
    {
        var existingByKey = new Dictionary<(int, int), PurchaseOrderLine>();
        foreach (var l in existingLines)
            existingByKey[(l.PositionNo, l.SequenceNo)] = l;   // last-wins (defensive against any legacy dup)

        var incomingByKey = new Dictionary<(int, int), PoLineRecord>();
        foreach (var l in incomingLines)
            incomingByKey[(l.PositionNo, l.SequenceNo)] = l;

        // Line ADDED — an incoming key with no persisted match (material: a new line changes the order).
        foreach (var key in incomingByKey.Keys)
            if (!existingByKey.ContainsKey(key))
                return true;

        // Line REMOVED — a persisted key absent from the push (material: the order shrank). Soft-handled by the
        // upsert, but still a material change for re-confirmation purposes (§6.4).
        foreach (var key in existingByKey.Keys)
            if (!incomingByKey.ContainsKey(key))
                return true;

        // Both sides share the same key set — compare the material fields line by line.
        foreach (var (key, incoming) in incomingByKey)
        {
            var existing = existingByKey[key];
            if (existing.OrderQty != incoming.OrderQty) return true;
            if (existing.PriceUnit != incoming.PriceUnit) return true;
            if (existing.Price != incoming.Price) return true;
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
        IReadOnlyCollection<PoLineRecord> incomingLines)
    {
        var beforeByKey = new Dictionary<(int, int), PoLineChangeSnapshot>();
        foreach (var l in beforeLines)
            beforeByKey[(l.PositionNo, l.SequenceNo)] = l;

        var incomingByKey = new Dictionary<(int, int), PoLineRecord>();
        foreach (var l in incomingLines)
            incomingByKey[(l.PositionNo, l.SequenceNo)] = l;

        var parts = new List<string>();

        foreach (var (key, incoming) in incomingByKey)
        {
            if (!beforeByKey.TryGetValue(key, out var before))
            {
                parts.Add($"Line {key.Item1} added (qty {Fmt(incoming.OrderQty)})");
                continue;
            }

            var changes = new List<string>();
            if (before.OrderQty != incoming.OrderQty)
            {
                var remaining = Math.Max(0m, incoming.OrderQty - before.ShippedQtyToDate);
                changes.Add($"qty {Fmt(before.OrderQty)}→{Fmt(incoming.OrderQty)} ({Fmt(remaining)} remaining)");
            }
            if (before.PriceUnit != incoming.PriceUnit)
                changes.Add($"unit price {Fmt(before.PriceUnit)}→{Fmt(incoming.PriceUnit)}");
            if (before.Price != incoming.Price)
                changes.Add($"price {Fmt(before.Price)}→{Fmt(incoming.Price)}");
            if (before.DeliveryDate != incoming.DeliveryDate)
                changes.Add($"delivery {FmtDate(before.DeliveryDate)}→{FmtDate(incoming.DeliveryDate)}");

            if (changes.Count > 0)
                parts.Add($"Line {key.Item1} {string.Join(", ", changes)}");
        }

        foreach (var (key, _) in beforeByKey)
            if (!incomingByKey.ContainsKey(key))
                parts.Add($"Line {key.Item1} removed");

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
