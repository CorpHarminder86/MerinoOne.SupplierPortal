namespace MerinoOne.SupplierPortal.Contracts.Payments;

public record PaymentListItemDto(
    Guid Id,
    int Seq,
    string PaymentReference,
    Guid InvoiceId,
    string InvoiceNumber,
    Guid SupplierId,
    string SupplierName,
    DateTime PaymentDate,
    decimal PaymentAmount,
    decimal NetPaid,
    string? PaymentMode,
    string? BankName);

public record PaymentDetailDto(
    Guid Id,
    int Seq,
    string PaymentReference,
    Guid InvoiceId,
    string InvoiceNumber,
    Guid SupplierId,
    string SupplierName,
    DateTime PaymentDate,
    decimal PaymentAmount,
    decimal NetPaid,
    string? PaymentMode,
    string? BankName,
    string? BankAccountRef,
    decimal TdsDeducted,
    string? TdsSection,
    string? Remarks,
    string? RemittancePdfUrl,
    string? ErpSyncId,
    DateTime CreatedOn);

public record RemittanceDto(
    Guid PaymentId,
    string? RemittancePdfUrl,
    string PaymentReference,
    decimal NetPaid,
    DateTime PaymentDate);
