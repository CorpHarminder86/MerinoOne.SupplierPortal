using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R5 (TSD R5 Addendum §4.4 / §8) — local, per-PO-line delivery commitment. Created when a PO becomes
/// shippable (one row per PO line in Phase 1). The remaining-to-ship is DERIVED at query time from the R4
/// line balance (orderQty − shippedQtyToDate); the schedule does not carry its own shipped ledger.
///
/// Indexes:
///   UX_DeliverySchedule_deliveryScheduleSeq  — clustered (via ApplyBaseEntityConvention)
///   IX_DeliverySchedule_shipTo_date          — (shipToAddressId, deliveryDate) — ASN creation filter
///   UQ_DeliverySchedule_line                 — UNIQUE FILTERED on (purchaseOrderLineId) WHERE isDeleted=0
///                                              (one active schedule per line in Phase 1; relaxed for
///                                              multi-schedule splits in Phase 2)
///
/// FKs all RESTRICT — do not cascade schedule deletions when a PO or line is soft-deleted.
/// </summary>
public class DeliverySchedule : BaseAggregateRoot
{
    /// <summary>FK → proc.PurchaseOrder (header linkage for queries/joins).</summary>
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    /// <summary>FK → proc.PurchaseOrderLine (the UNIQUE filtered key in Phase 1 — one schedule per line).</summary>
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    /// <summary>FK → admin.CompanyAddress (= PO ship-to, copied at schedule creation). Used as the grouping key
    /// for multi-line ASN creation (all selected lines must share this address — §9.1).</summary>
    public Guid ShipToAddressId { get; set; }
    public CompanyAddress? ShipToAddress { get; set; }

    /// <summary>= line.orderQty at creation (Phase 1). Refreshed by material Modify upsert (§8.2).</summary>
    public decimal ScheduledQty { get; set; }

    /// <summary>= line.deliveryDate at creation (Phase 1). Refreshed by material Modify upsert (§8.2).
    /// Persisted as datetime2 (time component always midnight UTC in Phase 1).</summary>
    public DateTime DeliveryDate { get; set; }

    /// <summary>Approved (Phase 1). String-persisted via HasConversion&lt;string&gt;. APPEND-ONLY.</summary>
    public DeliveryScheduleStatus Status { get; set; } = DeliveryScheduleStatus.Approved;
}
