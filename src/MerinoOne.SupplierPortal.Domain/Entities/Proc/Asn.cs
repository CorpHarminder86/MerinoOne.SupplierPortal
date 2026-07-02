using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Asn : BaseAggregateRoot
{
    public string AsnNumber { get; set; } = string.Empty;

    // R4 (2026-06-22) — Module 3 (Q1 multi-PO). An ASN may now span multiple POs, so the legacy single
    // scalar FK is NULLABLE and retained only for back-compat: new multi-PO ASNs use the AsnPurchaseOrder
    // junction below. Existing single-PO rows keep this FK; backend may migrate them into junction rows.
    // R5 (TSD R5 Addendum §4.5) — DEPRECATED as the primary grouping key. PO linkage moves to AsnLine
    // (via purchaseOrderLineId). Retained nullable for back-compat; never set on new ASNs created from
    // Delivery Schedule. The column and FK stay; the column was made nullable in migration 0019.
    public Guid? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    // R5 (TSD R5 Addendum §4.5 / §9) — ship-to grouping key. Every AsnLine under this ASN references a
    // PO line whose PurchaseOrder.shipToAddressId == this value; cross-ship-to lines are rejected at
    // selection time and at persist-time (the invariant in §9.3). Nullable while backfill is in progress
    // (existing ASNs have no ship-to); NOT NULL once backfill is complete and enforced via the application
    // layer for all new ASNs. FK → admin.CompanyAddress RESTRICT (a resolved ship-to must not be removed
    // out from under a historical ASN).
    public Guid? ShipToAddressId { get; set; }
    public CompanyAddress? ShipToAddress { get; set; }

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

    // R6 (2026-07-02) — outcome of the draft-invoice generation attempt at ASN approval:
    // "Generated" (drafts created) / "Blocked" (tax gap — no invoice created, note names the cause) / null
    // (never attempted, e.g. pre-R6 ASNs). String, not enum — 2 values + null, spec-typed as NVARCHAR.
    public string? InvoiceGenerationStatus { get; set; }
    public string? InvoiceGenerationNote { get; set; }

    public ICollection<AsnLine> Lines { get; set; } = new List<AsnLine>();

    // R4 (2026-06-22) — Module 3 (Q1): multi-PO junction. Child of the ASN aggregate (rows carry the ASN's
    // seccode via the root, not their own). Empty for legacy single-PO ASNs using PurchaseOrderId above.
    public ICollection<AsnPurchaseOrder> PurchaseOrders { get; set; } = new List<AsnPurchaseOrder>();
}
