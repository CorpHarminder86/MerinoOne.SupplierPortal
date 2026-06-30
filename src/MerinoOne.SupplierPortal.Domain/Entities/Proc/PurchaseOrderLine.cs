using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PurchaseOrderLine : AuditableEntity
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int PositionNo { get; set; }
    public int SequenceNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public string OrderUnit { get; set; } = "EA";
    public decimal OrderQty { get; set; }
    public decimal PriceUnit { get; set; }
    public decimal Price { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? TaxCode { get; set; }
    public string? TaxDescription { get; set; }

    // R4 (2026-06-22) — Addendum A2: link the free-string taxCode to the proc.Tax master. Keep taxCode /
    // taxDescription as the denormalized snapshot (PO-line is ERP-fed; the FK may point at a Tax row from an
    // unshared source company). FK → proc.Tax RESTRICT.
    public Guid? TaxId { get; set; }
    public Tax? Tax { get; set; }

    // R4 (2026-06-26) — TSD R4 Addendum §3.1, Component 1 (ASN Quantity Tracking). CUMULATIVE shipped quantity
    // across ALL non-cancelled ASN lines for this PO line (= Σ AsnLine.ShippedQty). This is a maintained
    // denormalisation written transactionally by the ASN create/cancel atomic guard — NEVER read-then-written.
    // The nominal balance (orderQty − shippedQtyToDate) is DERIVED at query time and never persisted.
    // DISTINCT from AsnLine.ShippedQty, which is "this ASN's ship qty" — the two must never be summed together.
    public decimal ShippedQtyToDate { get; set; }

    // R4 (2026-06-30) — last-received inbound additive delta echo (signed; may be negative to reduce). The
    // AUTHORITATIVE order quantity is always OrderQty: the inbound upsert applies the delta to OrderQty (OrderQty=0
    // & AdditionalQty≠0 → OrderQty += AdditionalQty) and stores the delta here for audit/traceability only. NOT a
    // running ledger of every add. NOT NULL DEFAULT 0 (EF auto-named default).
    public decimal AdditionalQty { get; set; }
}
