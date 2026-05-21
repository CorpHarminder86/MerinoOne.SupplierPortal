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
}
