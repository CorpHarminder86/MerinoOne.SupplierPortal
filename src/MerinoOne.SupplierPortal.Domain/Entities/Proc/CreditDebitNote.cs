using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class CreditDebitNote : BaseAggregateRoot
{
    public string NoteNumber { get; set; } = string.Empty;
    public NoteType NoteType { get; set; }
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public NoteStatus NoteStatus { get; set; } = NoteStatus.Draft;
}
