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
    DateTime CreatedOn,
    // R6 (2026-07-02) — provenance: "SupplierManual" (wizard) vs "AsnGenerated" (grouped ASN generator).
    string InvoiceOrigin = "SupplierManual");

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
    List<InvoiceLineDto> Lines,
    // R6 (2026-07-02) — provenance: "SupplierManual" (wizard) vs "AsnGenerated" (grouped ASN generator).
    string InvoiceOrigin = "SupplierManual");

/// <summary>One PO covered by a (possibly multi-PO) invoice, derived from the distinct PO lines.</summary>
public record InvoicePurchaseOrderDto(
    Guid PurchaseOrderId,
    string PoNumber);

// R6 (2026-07-02) — line-level tax snapshot (taxRatePct/taxId/taxDescription frozen on the row), the owning
// PO reference per line (multi-PO invoices), and RemainingQty = the LIVE invoiceable balance of the PO line
// (max(0, shippedQtyToDate − invoicedQtyToDate)) at READ time — the FE cap for Draft billedQty edits; 0 once
// the invoice is locked (non-Draft).
public record InvoiceLineDto(
    Guid Id,
    Guid PurchaseOrderLineId,
    string ItemCode,
    string? ItemDescription,
    decimal BilledQty,
    decimal UnitPrice,
    decimal LineAmount,
    string? TaxCode,
    decimal TaxAmount,
    decimal? TaxRatePct = null,
    Guid? TaxId = null,
    string? TaxDescription = null,
    Guid? PurchaseOrderId = null,
    string? PoNumber = null,
    decimal RemainingQty = 0);

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

// R4 (2026-06-22) — Module 4. Draft-only edit: header fields editable in Draft.
// R6 (2026-07-02) — optional Lines: per-line billedQty (server-capped at the LIVE shippedQtyToDate −
// invoicedQtyToDate; 400 over cap) + tax reselect (taxId is re-resolved server-side — code/description/rate are
// NEVER client-typed). LineAmount/TaxAmount + the header totals are recomputed server-side. Null Lines = header
// metadata edit only (back-compat).
public record UpdateInvoiceRequest(
    string InvoiceNumber,
    DateTime InvoiceDate,
    string? EInvoiceIrn,
    string? EInvoiceAckNo,
    string? EWayBillNumber,
    string? Notes,
    List<UpdateInvoiceLineRequest>? Lines = null);

// R6 (2026-07-02) — one Draft line edit. TaxId null = clear the line's tax (rate/description/amount reset);
// a supplied TaxId is re-resolved against the governed proc.Tax master (400 when it has no rate).
public record UpdateInvoiceLineRequest(
    Guid InvoiceLineId,
    decimal BilledQty,
    Guid? TaxId);

// R4 (2026-06-26) — Phase 4 / §8.3 / UC-ATT-03: AcknowledgeMissingAttachments confirms proceeding past any
// Warning-level attachment requirement on the Invoice entity. First submit (false) with a missing Warning
// attachment returns a 200 carrying ConfirmationRequired=true + the warning list; re-submit with true to proceed
// (audited). Mandatory-missing always blocks (400).
public record SubmitInvoiceRequest(bool AcknowledgeMissingAttachments = false);

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
