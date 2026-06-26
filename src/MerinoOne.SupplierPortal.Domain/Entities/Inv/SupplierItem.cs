using MerinoOne.SupplierPortal.Domain.Common;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Domain.Entities.Inv;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.3, Component 4 (Over-Ship Tolerance). Supplier-specific override of the
/// <see cref="Item.OverShipTolerancePct"/> floor. One row per (SupplierId, ItemId). Standard aggregate envelope
/// (two-key + audit + seccode RLS + tenant scope + rowVersion via <see cref="BaseAggregateRoot"/>).
///
/// <para><b>Nullable tolerance is load-bearing</b>: <see cref="OverShipTolerancePct"/> is NULLABLE so that
/// "no supplier-specific tolerance → inherit Item Master" (NULL) is distinguishable from "explicitly capped at
/// 0%" (0). The resolver is <c>SupplierItem?.OverShipTolerancePct ?? Item.OverShipTolerancePct</c> (§7.2). A
/// NOT NULL DEFAULT 0 here would silently force every pair to zero and defeat the fallback.</para>
///
/// <para><b>Forward-compatibility</b>: this is the intended home for future supplier-item attributes (supplier
/// part number, lead time, MOQ, supplier price). Built minimal now (tolerance only); do not create a second
/// supplier-item bridge later (§3.3).</para>
/// </summary>
public class SupplierItem : BaseAggregateRoot
{
    public Guid SupplierId { get; set; }
    public SupplierEntity? Supplier { get; set; }

    public Guid ItemId { get; set; }
    public Item? Item { get; set; }

    /// <summary>NULL ⇒ inherit <see cref="Item.OverShipTolerancePct"/>; 0 ⇒ explicit "no over-ship".</summary>
    public decimal? OverShipTolerancePct { get; set; }
}
