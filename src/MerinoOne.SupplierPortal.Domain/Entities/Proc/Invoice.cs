using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Invoice : BaseAggregateRoot
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid PurchaseOrderId { get; set; }
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
    public InvoiceStatus InvoiceStatus { get; set; } = InvoiceStatus.Submitted;
    public string? RejectionReason { get; set; }
    public string? EInvoiceIrn { get; set; }
    public string? EInvoiceAckNo { get; set; }
    public string? EWayBillNumber { get; set; }
    public string? SubmittedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}
