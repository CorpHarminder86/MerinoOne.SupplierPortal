namespace MerinoOne.SupplierPortal.Contracts.Invoices;

// R4 (2026-06-22) — Module 4 (Q1b: ONE invoice spans ALL the ASN's POs). PurchaseOrderId/PoNumber are NULLABLE
// (set for single-PO back-compat; null when the invoice spans POs — PO context lives on the lines). PoSummary
// gives a display string for the grid.
public record InvoiceListItemDto(
    Guid Id,
    int Seq,
    string InvoiceNumber,
    Guid? PurchaseOrderId,
    string? PoNumber,
    string PoSummary,
    int PoCount,
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

// R4 (2026-06-22) — Module 4. Nullable PO header (multi-PO), covered-PO list, posting-lifecycle fields
// (SubmittedAt, Revoked*, ErpPostedAt/ErpSyncId/ErpCode), IsLocked (true once Submitted — edits rejected),
// and RowVersion (base64) for the admin-revoke optimistic-concurrency guard (409 on stale).
public record InvoiceDetailDto(
    Guid Id,
    int Seq,
    string InvoiceNumber,
    Guid? PurchaseOrderId,
    string? PoNumber,
    IReadOnlyList<InvoicePurchaseOrderDto> PurchaseOrders,
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
    DateTime? SubmittedAt,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? RevokedBy,
    DateTime? RevokedAt,
    string? RevokeReason,
    DateTime? ErpPostedAt,
    string? ErpSyncId,
    string? ErpCode,
    bool IsLocked,
    string? RowVersion,
    string? Notes,
    DateTime CreatedOn,
    List<InvoiceLineDto> Lines);

/// <summary>One PO covered by a (possibly multi-PO) invoice, derived from the distinct PO lines.</summary>
public record InvoicePurchaseOrderDto(
    Guid PurchaseOrderId,
    string PoNumber);

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

// R4 (2026-06-22) — Module 4. Manual create-from-ASN trigger (the auto path runs inside SubmitAsnCommand).
public record CreateInvoiceFromAsnRequest(Guid AsnId);

// R4 (2026-06-22) — Module 4. Draft-only edit: header fields editable in Draft. Amounts/lines are inherited
// from the ASN at create; this edits the supplier-supplied invoice metadata + e-invoice fields in Draft.
public record UpdateInvoiceRequest(
    string InvoiceNumber,
    DateTime InvoiceDate,
    string? EInvoiceIrn,
    string? EInvoiceAckNo,
    string? EWayBillNumber,
    string? Notes);

public record SubmitInvoiceRequest();

// R4 (2026-06-22) — Module 4 (Q9): admin pre-post revoke (Submitted -> Draft). RowVersion (base64, from the
// detail DTO) is required for the optimistic-concurrency guard — a stale token yields 409.
public record RevokeInvoiceRequest(string? Reason, string? RowVersion);

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
