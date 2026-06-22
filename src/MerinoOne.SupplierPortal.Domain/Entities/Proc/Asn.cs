using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Asn : BaseAggregateRoot
{
    public string AsnNumber { get; set; } = string.Empty;

    // R4 (2026-06-22) — Module 3 (Q1 multi-PO). An ASN may now span multiple POs, so the legacy single
    // scalar FK is NULLABLE and retained only for back-compat: new multi-PO ASNs use the AsnPurchaseOrder
    // junction below. Existing single-PO rows keep this FK; backend may migrate them into junction rows.
    public Guid? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime ExpectedDeliveryDate { get; set; }
    public string? TimeWindow { get; set; }
    public string? CarrierName { get; set; }
    public string? TrackingNumber { get; set; }
    public string? VehicleNumber { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhone { get; set; }

    // R4 (2026-06-22) — Module 3: draft/submit lifecycle. Default flipped Submitted → Draft so a new ASN is a
    // draft until the supplier submits; submit stamps submittedAt/By, posts to ERP via the Increment-0 outbox
    // (erpSyncId = outbox correlation), and the ERP ack writes back erpCode (the ASNNo) via /inbound/erp-ack.
    public AsnStatus AsnStatus { get; set; } = AsnStatus.Draft;
    public string? Notes { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? ErpSyncId { get; set; }
    public string? ErpCode { get; set; }

    public ICollection<AsnLine> Lines { get; set; } = new List<AsnLine>();

    // R4 (2026-06-22) — Module 3 (Q1): multi-PO junction. Child of the ASN aggregate (rows carry the ASN's
    // seccode via the root, not their own). Empty for legacy single-PO ASNs using PurchaseOrderId above.
    public ICollection<AsnPurchaseOrder> PurchaseOrders { get; set; } = new List<AsnPurchaseOrder>();
}
