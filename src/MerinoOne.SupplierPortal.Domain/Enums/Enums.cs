namespace MerinoOne.SupplierPortal.Domain.Enums;

public enum SeccodeType { U, G }

public enum RegistrationStatus
{
    Invited,
    Registering,
    Submitted,
    UnderReview,
    Approved,
    Rejected,
    Active,
    Inactive,
    Suspended
}

public enum SupplierType { Material, Service, Both }

public enum VerificationType { GST, PAN, MSME }
public enum VerificationResult { Pass, Fail, Error }

public enum PoType { Material, Service }
public enum PoStatus
{
    Draft,
    Released,
    Acknowledged,
    Accepted,
    Rejected,
    DateProposed,
    PartiallyDelivered,
    Delivered,
    Closed,
    Cancelled,
    // R4 (2026-06-24) — PO Negotiation. Persisted as the enum name (string), no DB CHECK on poStatus — the C#
    // enum is the guard. Negotiation = an open supplier negotiation is in flight; Approved = buyer approved the
    // negotiated terms (ERP re-syncs the revised PO inbound; local lines are NOT mutated). APPEND-ONLY.
    Negotiation,
    Approved
}

/// <summary>
/// R4 (2026-06-24) — PO Negotiation lifecycle. Submitted = supplier raised the negotiation (open); the buyer
/// resolves it to Approved or Rejected, or the supplier withdraws it to Cancelled. Persisted as the enum name
/// (string), no DB CHECK — the C# enum is the guard. APPEND-ONLY.
/// </summary>
public enum PoNegotiationStatus { Submitted, Approved, Rejected, Cancelled }

public enum ScheduleStatus { Proposed, Approved, Rejected, Cancelled }

public enum AsnStatus { Draft, Submitted, InTransit, Delivered, Cancelled }

public enum MatchingType { TwoWay, ThreeWay }
public enum InvoiceStatus
{
    Draft,
    Submitted,
    UnderReview,
    Matched,
    MatchExceptions,
    Approved,
    Rejected,
    Paid,
    PartiallyPaid,
    Cancelled
}

public enum NoteType { CN, DN }
public enum NoteStatus { Draft, Submitted, Approved, Rejected }

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D (Q5). Goods-receipt approval state, ERP-owned (LN pushes it via
/// /inbound/grn-status). An invoice posts only when ALL covering GRN lines reach <c>GrnApproved</c>. Persisted
/// as the enum name (string), no DB CHECK — the C# enum is the guard. APPEND-ONLY.
/// </summary>
public enum GrnStatus { GrnNotApproved, GrnApproved, Rejected }

public enum AiValidationStatus { Pending, Valid, Flagged, Skipped }

public enum DocumentType
{
    Invoice,
    PackingSlip,
    TestCertificate,
    EInvoice,
    EWayBill,
    OnboardingPan,
    OnboardingGst,
    OnboardingCheque,
    OnboardingMsme,
    // R4 (2026-06-22): supplier-license attachments + ASN multi-attachments. documentType has NO DB CHECK
    // (only aiValidationStatus does) so these are additive enum values — no migration needed. APPEND-ONLY.
    License,
    AsnAttachment
}

/// <summary>
/// Supplier PO-response behaviour. <c>Manual</c> = supplier explicitly Acknowledges/Accepts/Rejects each PO;
/// <c>Auto</c> = the portal auto-acknowledges + auto-confirms the delivery date + posts the acceptance to ERP
/// at PO release (server-side hook, owned by backend-developer). Persisted as the enum name (string), no CHECK.
/// APPEND-ONLY.
/// </summary>
public enum PoResponseMode { Manual, Auto }

// R4 (2026-06-22) — Module 2 (Supplier Change Management). All persisted as the enum name (string), no DB CHECK
// — the C# enum is the guard (matches the dominant status-enum convention). APPEND-ONLY.

/// <summary>
/// Supplier change-request lifecycle. Draft/ChangesRequested are supplier-editable; Submitted→UnderReview→
/// Approved/Rejected is the review path; Approved applies the deltas then the per-line push rolls the request
/// up to Pushed (all) / PartiallyPushed (some) / PushFailed (none).
/// </summary>
public enum ChangeRequestStatus
{
    Draft,
    Submitted,
    UnderReview,
    ChangesRequested,
    Approved,
    Rejected,
    Pushed,
    PartiallyPushed,
    PushFailed
}

/// <summary>Which supplier sub-entity a change-request line targets.</summary>
public enum ChangeTargetEntity { Supplier, Address, Contact, Bank, License }

/// <summary>The kind of mutation a change-request line describes.</summary>
public enum ChangeOperation { Add, Edit, Delete }

/// <summary>Per-line ERP push state within an approved change request.</summary>
public enum LinePushStatus { Pending, Pushed, PushFailed }

/// <summary>
/// Transactional-outbox row lifecycle for the post-commit ERP dispatch helper (Increment 0). Persisted as the
/// enum name (string), no CHECK — the C# enum is the guard (matches the dominant status-enum convention).
/// APPEND-ONLY.
///
/// <para>Flow: <c>Pending</c> (enqueued) → <c>Sending</c> (the dispatcher won the atomic per-row claim and is
/// about to POST — the claim commits BEFORE the ERP call so a restart/scale-out loses the optimistic-concurrency
/// race rather than double-POSTing) → <c>Dispatched</c> (POST succeeded) → <c>Acked</c> (ERP echoed the ack on
/// <c>/inbound/erp-ack</c>). A POST failure rolls the row <c>Sending → Failed</c> (retryable). A crash mid-POST
/// strands the row in <c>Sending</c>; the worker's stale-<c>Sending</c> sweep resets it to <c>Pending</c> by age
/// so it auto-recovers (the deterministic key is reused, so LN dedupes the re-POST).</para>
/// </summary>
public enum OutboxStatus { Pending, Dispatched, Acked, Failed, Sending }

public enum SyncDirection { Inbound, Outbound, Bidirectional }
public enum SyncStatus { Pending, Success, Failed, Retrying, Reconciled }

/// <summary>
/// Master-data endpoints that participate in company-wise table sharing. Persisted as a
/// string (enum name) so the value is stable across reordering. APPEND-ONLY.
/// </summary>
public enum SharedEndpoint { PaymentTerm, DeliveryTerm, Unit, ItemGroup, Item, Tax }

/// <summary>
/// Tenant-scoped reference masters fed by Infor LN that do NOT participate in company sharing. Drives the
/// inbound endpoint-gate EntityName for the tenant inbound path. Persisted as the enum name. APPEND-ONLY.
/// </summary>
public enum TenantInboundEntity { Currency, Country, State, City, PostalCode }

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D. Transactional inbound entities fed by Infor LN that are neither
/// company-shared masters (<see cref="SharedEndpoint"/>) nor tenant reference masters
/// (<see cref="TenantInboundEntity"/>) — they hang off live transactions (GRN status, Payment, invoice-status,
/// ERP ack). Drives the inbound endpoint-gate <c>InforEndpointMap.EntityName</c> for the transactional inbound
/// path (/inbound/grn-status, /inbound/payments, /inbound/invoice-status, /inbound/erp-ack). Backend extends the
/// executor's EntityName resolution to recognise this set. Persisted as the enum name. APPEND-ONLY.
/// </summary>
// R4 (2026-06-23) — APPENDED: Po / DeliverySchedule / GrnReceipt are the transactional DOCUMENT-ingestion
// entities (CREATE/UPSERT the live PO + delivery schedule + goods-receipt rows; the existing Grn entry is the
// status-UPDATE path). Each drives its own InforEndpointMap gate + Integration.Inbound.* scope.
public enum TransactionalInboundEntity { Grn, Payment, InvoiceStatus, ErpAck, Po, DeliverySchedule, GrnReceipt }

/// <summary>Unit dimension / quantity type (INFOR LN unit type).</summary>
public enum UnitType { Quantity, Length, Area, Volume, Mass, Time, Other }
