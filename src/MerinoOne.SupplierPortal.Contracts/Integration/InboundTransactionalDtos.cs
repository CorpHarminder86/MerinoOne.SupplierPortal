using System.Text.Json.Serialization;

namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R4 (2026-06-22) — Module 5 / Increment D. Inbound request bodies for the transactional ERP inbound loop:
// GRN status (the auto-post trigger), Payment write-back (H1), invoice-status advance (H1) and the ERP-ack
// write-back (erpCode round-trip). All four endpoints are X-APIKey + per-endpoint Integration.Inbound.* scope
// and reuse the InboundUpsertExecutor / TenantInboundUpsertExecutor (company resolution, anti-spoof,
// canonical-hash idempotency, InforSyncLog / IntegrationError, endpoint-session telemetry).

// ----------------------------------- GRN status -----------------------------------

/// <summary>
/// One inbound goods-receipt status record pushed by Infor LN. <see cref="GrnNumber"/> is the natural key
/// (within the resolved company). <see cref="GrnStatus"/> is the enum NAME (GrnNotApproved / GrnApproved /
/// Rejected). <see cref="AsnNumber"/> / <see cref="AsnErpRef"/> let the writer resolve+stamp the deterministic
/// GoodsReceipt→Invoice link (GoodsReceipt.AsnId → the Invoice with that asnId). GRN status is ERP-owned.
/// </summary>
public record GrnStatusRecord(
    string GrnNumber,
    string GrnStatus,
    string? ErpSyncId = null,
    decimal? ReceivedQty = null,
    string? AsnNumber = null,
    string? AsnErpRef = null,
    string? IssueReported = null,
    string? ErpCode = null);

/// <summary>
/// Inbound GRN-status push body. <see cref="CompanyCode"/> is the Infor LN logistic company code; it is
/// resolved to a TenantEntity within the key's tenant (no share-group normalization — GRNs belong to the
/// literal company). Unknown company ⇒ 400; resolves to a company the key is not bound to ⇒ 403.
/// </summary>
public record PushGrnStatusRequest(
    string CompanyCode,
    IReadOnlyList<GrnStatusRecord> Receipts);

// ----------------------------------- Payments (H1) -----------------------------------

/// <summary>
/// One inbound payment / remittance record pushed by Infor LN. The invoice is resolved by
/// <see cref="ErpSyncId"/> (preferred) else <see cref="InvoiceNumber"/>. <see cref="NetPaid"/> is the
/// received amount (Σ NetPaid per invoice = Payment Summary "Received amount"). <see cref="PaymentReference"/>
/// is the natural key for idempotent re-push (upsert on (invoiceId, paymentReference)).
/// </summary>
public record PaymentRecord(
    string PaymentReference,
    decimal NetPaid,
    string? InvoiceErpSyncId = null,
    string? InvoiceNumber = null,
    decimal? PaymentAmount = null,
    decimal? TdsDeducted = null,
    DateTime? PaymentDate = null,
    string? PaymentMode = null,
    string? ErpSyncId = null,
    string? ErpCode = null);

/// <summary>Inbound Payment push body. See <see cref="PushGrnStatusRequest"/> for the company semantics.</summary>
public record PushPaymentsRequest(
    string CompanyCode,
    IReadOnlyList<PaymentRecord> Payments);

// ----------------------------------- Invoice status (H1) -----------------------------------

/// <summary>
/// One inbound invoice-status record pushed by Infor LN. The invoice is resolved by <see cref="ErpSyncId"/>
/// (preferred) else <see cref="InvoiceNumber"/>. <see cref="InvoiceStatus"/> is the enum NAME and is
/// constrained by the writer to the ERP-driven advance set (Matched / PartiallyPaid / Paid / Approved /
/// MatchExceptions / Rejected) — the writer never moves an invoice backwards into Draft/Submitted.
/// </summary>
public record InvoiceStatusRecord(
    string InvoiceStatus,
    string? InvoiceErpSyncId = null,
    string? InvoiceNumber = null,
    string? ErpCode = null);

/// <summary>Inbound invoice-status push body. See <see cref="PushGrnStatusRequest"/> for the company semantics.</summary>
public record PushInvoiceStatusRequest(
    string CompanyCode,
    IReadOnlyList<InvoiceStatusRecord> Invoices);

// ----------------------------------- ERP ack (erpCode write-back) -----------------------------------

/// <summary>
/// The ERP acknowledgement write-back for a Portal→ERP transaction. <see cref="PortalRef"/> is the
/// deterministic outbox correlation id (<c>sha256("&lt;entity&gt;|&lt;businessKey&gt;|&lt;op&gt;")</c>) that
/// the portal echoed on the outbound post. On <see cref="Success"/> the writer resolves it to EXACTLY ONE
/// pending outbox row of <see cref="TransactionType"/>, writes <see cref="ErpCode"/> to the matching record
/// (Supplier→SupCode, Asn→ASNNo, Invoice/GoodsReceipt/Payment/Address/Contact/Bank/License/change-line) and
/// flips the outbox row to Acked. Idempotent on re-ack; mismatch ⇒ IntegrationError, no write (risk R17).
/// Tenant-scoped (no CompanyCode) — the outbox row is keyed by tenant + deterministic key.
/// </summary>
public record ErpAckRecord(
    string TransactionType,
    string PortalRef,
    bool Success,
    string? ErpCode = null,
    string? Message = null,
    // R8 (2026-07-04) — TSD R8 §3.2 / D1+D2. LN returns the ERP composite key on Invoice/ASN acks; written to
    // proc.Invoice / proc.Asn to feed the IDM eligibility gate. Optional + trailing = non-breaking for existing
    // callers. Changing any on an already-synced owner auto-enqueues IDM Update ops (D4b).
    string? ErpCompany = null,
    string? ErpTransactionType = null,
    string? ErpDocumentNo = null);

/// <summary>Inbound ERP-ack push body (batch of one or more acks).</summary>
public record PushErpAckRequest(
    IReadOnlyList<ErpAckRecord> Acks);

// ----------------------------------- GRN-status command result -----------------------------------

/// <summary>
/// Per-receipt outcome of the GRN-status cascade. <see cref="AutoPostEnqueued"/> flags the receipts whose
/// transition-into-GrnApproved completed an invoice's all-covering-GRN set and enqueued the invoice post on
/// the outbox; <see cref="ReverseTransitionAlert"/> flags a GrnApproved→NotApproved/Rejected correction on a
/// GRN whose invoice was already posted (operator alert raised, NO auto un-post).
/// </summary>
public record GrnStatusRowResult(
    string GrnNumber,
    RowOutcome Outcome,
    string? Error,
    bool AutoPostEnqueued = false,
    bool ReverseTransitionAlert = false);

/// <summary>
/// Result of an inbound GRN-status batch. Extends the generic <see cref="UpsertResultDto"/> shape with the
/// cascade telemetry (auto-posts enqueued + reverse-transition alerts) so the caller / SyncLog viewer can see
/// the financial side-effects of the batch.
/// </summary>
public record UpsertGrnStatusResultDto(
    string CompanyCode,
    int Received,
    int Inserted,
    int Updated,
    int Skipped,
    int Failed,
    int AutoPostsEnqueued,
    int ReverseTransitionAlerts,
    IReadOnlyList<GrnStatusRowResult> Rows);
