namespace MerinoOne.SupplierPortal.Contracts.Invoices;

public record InvoiceListItemDto(
    Guid Id,
    int Seq,
    string InvoiceNumber,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    DateTime InvoiceDate,
    decimal InvoiceAmount,
    decimal TaxAmount,
    decimal NetAmount,
    string CurrencyCode,
    string MatchingType,
    string InvoiceStatus,
    DateTime CreatedOn);

public record InvoiceDetailDto(
    Guid Id,
    int Seq,
    string InvoiceNumber,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid? AsnId,
    string? AsnNumber,
    Guid SupplierId,
    string SupplierName,
    string SupplierCode,
    DateTime InvoiceDate,
    decimal InvoiceAmount,
    decimal TaxAmount,
    decimal NetAmount,
    string CurrencyCode,
    string MatchingType,
    string? GrnReference,
    string InvoiceStatus,
    string? RejectionReason,
    string? EInvoiceIrn,
    string? EInvoiceAckNo,
    string? EWayBillNumber,
    string? SubmittedBy,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? Notes,
    DateTime CreatedOn,
    List<InvoiceLineDto> Lines);

public record InvoiceLineDto(
    Guid Id,
    Guid PurchaseOrderLineId,
    string ItemCode,
    string? ItemDescription,
    decimal BilledQty,
    decimal UnitPrice,
    decimal LineAmount,
    string? TaxCode,
    decimal TaxAmount);

public record CreateInvoiceRequest(
    Guid PurchaseOrderId,
    Guid? AsnId,
    string InvoiceNumber,
    DateTime InvoiceDate,
    decimal InvoiceAmount,
    decimal TaxAmount,
    decimal NetAmount,
    string CurrencyCode,
    string MatchingType,
    string? EInvoiceIrn,
    string? EInvoiceAckNo,
    string? EWayBillNumber,
    string? Notes,
    List<CreateInvoiceLineRequest> Lines);

public record CreateInvoiceLineRequest(
    Guid PurchaseOrderLineId,
    string ItemCode,
    string? ItemDescription,
    decimal BilledQty,
    decimal UnitPrice,
    decimal LineAmount,
    string? TaxCode,
    decimal TaxAmount);

public record ReviewInvoiceRequest();
public record ApproveInvoiceRequest(string? Notes = null);
public record RejectInvoiceRequest(string Reason);

public record CreditDebitNoteListItemDto(
    Guid Id,
    int Seq,
    string NoteNumber,
    string NoteType,
    Guid InvoiceId,
    string InvoiceNumber,
    decimal Amount,
    string NoteStatus,
    DateTime CreatedOn);

public record CreditDebitNoteDetailDto(
    Guid Id,
    int Seq,
    string NoteNumber,
    string NoteType,
    Guid InvoiceId,
    string InvoiceNumber,
    Guid SupplierId,
    string SupplierName,
    decimal Amount,
    string? Reason,
    string NoteStatus,
    DateTime CreatedOn);

public record CreateCreditDebitNoteRequest(
    Guid InvoiceId,
    string NoteType,
    string NoteNumber,
    decimal Amount,
    string? Reason);

public record ApproveCreditDebitNoteRequest();
