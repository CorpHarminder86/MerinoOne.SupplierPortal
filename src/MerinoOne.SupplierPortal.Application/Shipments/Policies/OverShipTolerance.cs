using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Application.Shipments.Policies;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §7 (Component 4, Over-Ship Tolerance). Pure, trivially unit-testable
/// two-tier tolerance resolver (UC-ASN-05 cases A–D). The over-ship ceiling the atomic guard enforces is
/// <c>orderQty × (1 + resolved%/100) − shippedQtyToDate</c> (§7.3).
///
/// <para><b>Resolution (§7.1):</b> <c>SupplierItem.OverShipTolerancePct ?? Item.OverShipTolerancePct</c>. The
/// nullability of the supplier-item override is load-bearing — a present-but-NULL override INHERITS the Item
/// Master value (Case B), whereas an explicit 0 caps at "no over-ship" (Case D). <see cref="Item.OverShipTolerancePct"/>
/// is NOT NULL (default 0), so the fallback always resolves to a defined value.</para>
/// </summary>
public static class OverShipTolerance
{
    /// <summary>
    /// Resolves the effective over-ship tolerance percent for a (supplier, item) pair.
    /// <list type="bullet">
    ///   <item>Case A — SupplierItem present with a non-null value → that value (override wins).</item>
    ///   <item>Case B — SupplierItem present but value NULL → inherit <paramref name="item"/>.OverShipTolerancePct.</item>
    ///   <item>Case C — no SupplierItem row (<paramref name="si"/> null) → inherit <paramref name="item"/>.OverShipTolerancePct.</item>
    ///   <item>Case D — SupplierItem present with explicit 0 → 0 (no over-ship).</item>
    /// </list>
    /// </summary>
    public static decimal ResolveOverShipTolerance(SupplierItem? si, Item item)
        => si?.OverShipTolerancePct ?? item.OverShipTolerancePct;   // item.* is NOT NULL (default 0).

    /// <summary>The multiplicative ceiling factor for the resolved tolerance: <c>1 + tol%/100</c>.</summary>
    public static decimal Factor(decimal tolerancePct) => 1m + (tolerancePct / 100m);
}
