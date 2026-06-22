using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Invoice : BaseAggregateRoot
{
    public string InvoiceNumber { get; set; } = string.Empty;

    // R4 (2026-06-22) — Module 4 (Q1b: ONE invoice covers ALL the ASN's POs). PO context now lives on
    // InvoiceLine.purchaseOrderLineId (which already spans POs), so the header FK is NULLABLE. The invoice
    // stays 1:1 with its ASN via AsnId (UQ_Invoice_asnId → exactly one draft invoice per ASN). Retained
    // (nullable) for back-compat with existing single-PO invoices.
    public Guid? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public Guid? AsnId { get; set; }
    public Asn? Asn { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime InvoiceDate { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string CurrencyCode { get; set; } = "INR";
    public MatchingType MatchingType { get; set; } = MatchingType.ThreeWay;
    public string? GrnReference { get; set; }

    // R4 (2026-06-22) — Module 4: posting lifecycle. Default flipped Submitted → Draft (an ASN-originated
    // invoice starts as an editable draft). NOTE for backend: the existing CreateInvoiceCommand currently
    // creates 'Submitted' and enqueues a post — refactor it off auto-post-on-create in Increment B; this
    // change is schema/default only.
    public InvoiceStatus InvoiceStatus { get; set; } = InvoiceStatus.Draft;
    public string? RejectionReason { get; set; }
    public string? EInvoiceIrn { get; set; }
    public string? EInvoiceAckNo { get; set; }
    public string? EWayBillNumber { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? Notes { get; set; }

    // R4 (2026-06-22) — Module 4: admin pre-post revoke (Submitted → Draft) + ERP posting ack write-back.
    // RowVersion (via IHasRowVersion on BaseAggregateRoot) guards the revoke/auto-post concurrency race.
    // erpCode populated via /inbound/erp-ack; erpPostedAt/erpSyncId stamped by the GRN-gated auto-post path.
    public string? RevokedBy { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public DateTime? ErpPostedAt { get; set; }
    public string? ErpSyncId { get; set; }
    public string? ErpCode { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
