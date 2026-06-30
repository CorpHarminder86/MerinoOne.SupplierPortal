using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class AsnLine : AuditableEntity
{
    public Guid AsnId { get; set; }
    public Asn? Asn { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal ShippedQty { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }

    // R4 (2026-06-22) — Addendum A4: snapshot of the source PO line's PositionNo/SequenceNo (shown while
    // building the ASN, and required stable in the LN ASN post payload even if the PO line later changes).
    public int? PositionNo { get; set; }
    public int? SequenceNo { get; set; }

    // R5 (TSD R5 Addendum §4.5 / §9.2) — back-link to the originating Delivery Schedule line. Nullable:
    // legacy ASN lines (pre-R5, created from a PO picker) have no schedule; R5 lines created from the
    // Delivery Schedule grid carry the scheduleId for traceability and for the balance/UX checks.
    // FK → proc.DeliverySchedule RESTRICT (a schedule must not be removed if shipment lines reference it).
    public Guid? DeliveryScheduleId { get; set; }
    public DeliverySchedule? DeliverySchedule { get; set; }

    // R4 (2026-06-23) — Serial/Lot capture. CHILD collections of the ASN aggregate (reached via this line):
    // populated only for a serialized item (Serials) or a lot-controlled item (Lots) — mutually exclusive
    // per item, enforced by the Item XOR guard. CASCADE-deleted with the line.
    public ICollection<AsnLineSerial> Serials { get; set; } = new List<AsnLineSerial>();
    public ICollection<AsnLineLot> Lots { get; set; } = new List<AsnLineLot>();
}
