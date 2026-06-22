using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Payment : BaseAggregateRoot
{
    public string PaymentReference { get; set; } = string.Empty;
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime PaymentDate { get; set; }
    public decimal PaymentAmount { get; set; }
    public string? PaymentMode { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountRef { get; set; }
    public decimal TdsDeducted { get; set; }
    public string? TdsSection { get; set; }
    public decimal NetPaid { get; set; }
    public string? Remarks { get; set; }
    public string? RemittancePdfUrl { get; set; }
    public string? ErpSyncId { get; set; }

    // R4 (2026-06-22) — Module 5 / Increment D (H1: inbound Payment sync). ERP ack write-back code, set by
    // /inbound/erp-ack. InvoiceId (+ FK_Payment_Invoice_InvoiceId RESTRICT), PaymentReference and ErpSyncId
    // already shipped on this entity; the inbound writer correlates on ErpSyncId and links to the invoice via
    // InvoiceId. Received amount = Σ NetPaid per invoice (no stored column); Payment Due Date is derived
    // (invoiceDate + PaymentTerm.netDays — no column).
    public string? ErpCode { get; set; }
}
