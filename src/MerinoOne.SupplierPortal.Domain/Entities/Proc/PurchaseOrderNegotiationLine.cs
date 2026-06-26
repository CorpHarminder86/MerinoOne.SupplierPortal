using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R4 (2026-06-24) — a single changed line within a <see cref="PurchaseOrderNegotiation"/> (mirrors
/// SupplierChangeRequestLine). One row per PO line whose qty/delivery date the supplier proposes to change.
/// Modeled as a plain <see cref="AuditableEntity"/> (child, accessed ONLY via the parent aggregate, never as a
/// root DbSet): it carries no seccode of its own — RLS is enforced on the parent. Carries both the original
/// snapshot (qty + delivery date at submit) and the negotiated values so the buyer diff view sources the delta
/// from the negotiation, not the live PO lines (which are never mutated by this feature).
/// </summary>
public class PurchaseOrderNegotiationLine : AuditableEntity
{
    public Guid PurchaseOrderNegotiationId { get; set; }
    public PurchaseOrderNegotiation? PurchaseOrderNegotiation { get; set; }

    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    /// <summary>Natural-key snapshot from the source PO line (display + ERP BOD mapping).</summary>
    public int PositionNo { get; set; }
    public int SequenceNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>The PO line qty at submit time — the original snapshot for the buyer diff.</summary>
    public decimal OriginalQty { get; set; }

    /// <summary>The qty the supplier is proposing.</summary>
    public decimal NegotiatedQty { get; set; }

    /// <summary>The PO line delivery date at submit time — original snapshot for the buyer diff.</summary>
    public DateTime? OriginalDeliveryDate { get; set; }

    /// <summary>The delivery date the supplier is proposing.</summary>
    public DateTime? NegotiatedDeliveryDate { get; set; }

    /// <summary>The PO line unit price (PriceUnit) at submit time — original snapshot for the buyer diff.</summary>
    public decimal OriginalPrice { get; set; }

    /// <summary>The unit price the supplier is proposing.</summary>
    public decimal NegotiatedPrice { get; set; }
}
