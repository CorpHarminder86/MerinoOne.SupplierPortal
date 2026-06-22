using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class GoodsReceipt : BaseAggregateRoot
{
    public string GrnNumber { get; set; } = string.Empty;
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public Guid? AsnId { get; set; }
    public Asn? Asn { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal ShortQty { get; set; }
    public decimal RejectedQty { get; set; }
    public DateTime GrnDate { get; set; } = DateTime.UtcNow;
    public string? ErpSyncId { get; set; }

    // R4 (2026-06-22) — Module 5 / Increment D (Q5: GRN webhook + all-covering-GRNs grain). GRN status is
    // ERP-owned (no portal manual approval); LN pushes it via /inbound/grn-status. Persisted as the enum name
    // (string), NO DB CHECK — the C# enum is the guard (dominant status-enum convention). Default 'GrnNotApproved'.
    public GrnStatus GrnStatus { get; set; } = GrnStatus.GrnNotApproved;
    public DateTime? GrnApprovedAt { get; set; }

    // Deterministic GRN→Invoice link (replaces the brittle Invoice.GrnReference string). Populated when the
    // ASN's draft invoice is created; drives the all-covering-GRNs auto-post cascade. NULL at migration time.
    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    // Q7-1: Payment Summary "Issue Reported" source. ERP-fed remark on the receipt.
    public string? IssueReported { get; set; }

    // ERP ack write-back (/inbound/erp-ack) — the ERP-side GRN code.
    public string? ErpCode { get; set; }
}
