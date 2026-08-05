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

    // R13 (2026-08-05) — WAREHOUSE grouping key (replaces the retired ship-to). Every AsnLine under this ASN
    // references a PO line whose WarehouseAddressId == this value; cross-warehouse lines are rejected at selection
    // time and persist-time (the §9.3 invariant, now one-warehouse-per-ASN). Resolved from the covered PO lines'
    // single warehouse. FK → admin.CompanyAddress RESTRICT; the WarehouseAddress snapshot renders the receiving
    // address without a join. Nullable (existing ASNs have no warehouse address). The raw Warehouse code below is
    // the value that ships to LN on the ASN post.
    public Guid? WarehouseAddressId { get; set; }
    public CompanyAddress? WarehouseAddress { get; set; }
    public WarehouseSnapshot? WarehouseAddressSnapshot { get; set; }

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
    // R11 (2026-07-28, D4) — receiving warehouse, snapshotted from the covered PurchaseOrder.Warehouse at
    // create/update. A GROUPING KEY exactly like ShipToAddressId above: every AsnLine under this ASN references
    // a PO line whose PurchaseOrder.Warehouse == this value, and a cross-warehouse line selection is rejected
    // at selection time and at persist time. Nullable — legacy ASNs and ASNs whose POs predate the warehouse
    // field have none. Free-text code (no FK), so this is a snapshot, not a reference.
    public string? Warehouse { get; set; }

    // R11 (2026-07-28, D6/D7) — supplier-entered shipment references, forwarded verbatim to LN. All three are
    // mandatory at Send-For-Approval (NOT at draft creation, D7) and editable in Draft/Rejected only (D9), so
    // nothing reaches the ERP without them while the "park a partial draft" flow survives. Pure LN passthrough
    // (D13): InvoiceNo is the supplier's own commercial invoice reference and has NO link to the R6 invoice
    // pipeline, which keeps numbering generated drafts DRAFT-{asnNumber}-{groupSeq}.
    public string? InvoiceNo { get; set; }
    public string? BillOfLading { get; set; }
    public string? PackingList { get; set; }

    public AsnStatus AsnStatus { get; set; } = AsnStatus.Draft;
    public string? Notes { get; set; }

    // NOTE (R11): submittedAt is stamped ONLY at AsnSubmitExecutor:348, whose only caller is the buyer's
    // ApproveAsnCommandHandler — it shares that handler's `now` with AsnApproval.DecisionOn. So it means
    // "submitted TO ERP at buyer approval", not "submitted by the supplier", and it IS the LN payload's
    // ShipmentDate (D11). The supplier's SendForApproval does not touch it.
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? ErpSyncId { get; set; }
    public string? ErpCode { get; set; }

    // R11.2 (2026-07-29) — the R8 LN composite key (ErpCompany/ErpTransactionType/ErpDocumentNo) was REMOVED
    // from the ASN (migration 0055; Invoice keeps its trio). User decision: erpCompany always equals the
    // company code already held via TenantEntityId, and the IDM ASN mapping that consumed the other two was
    // wrong and is being replaced (config held in the meantime). The IDM eligibility signal for ASN documents
    // is now ErpCode alone (the LN ASNNo written back by /inbound/erp-ack).

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
