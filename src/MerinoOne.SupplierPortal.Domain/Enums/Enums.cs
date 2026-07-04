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
    // R4 (2026-06-26) — TSD R4 Addendum §3.5 / D2: DateProposed REMOVED. The old date-only "propose a delivery
    // date" flow is retired; ALL qty/price/date counter-proposals now flow through the single PurchaseOrderNegotiation
    // aggregate (PoStatus.Negotiation). 2b data-migrates any residual DateProposed rows → Released. (gap left here so
    // existing persisted enum-name strings keep their position — string-persisted, no DB CHECK; the C# enum is the
    // guard. The removed member was between Rejected and PartiallyDelivered.)
    PartiallyDelivered,
    // R5 (TSD R5 Addendum §4.7 / §11.2) — all PO lines shipped (shippedQtyToDate == orderQty for every
    // active line), awaiting ERP receipt confirmation. Portal-derived: set by the ASN Submit balance check,
    // NOT by any ERP inbound mapping. Persisted as the enum name (string), no DB CHECK; APPEND-ONLY.
    // Gate note (R4 §6.2): FullyShipped is balance-driven like PartiallyDelivered — no remaining qty, so
    // no new ASNs can be created. Delivered (ERP-driven) is the next milestone after FullyShipped.
    FullyShipped,
    Delivered,
    Closed,
    Cancelled,
    // R4 (2026-06-24) — PO Negotiation. Persisted as the enum name (string), no DB CHECK on poStatus — the C#
    // enum is the guard. Negotiation = an open supplier negotiation is in flight; Approved = buyer approved the
    // negotiated terms (ERP re-syncs the revised PO inbound; local lines are NOT mutated). APPEND-ONLY.
    // R4 (2026-06-26) — D2 / §6.2: BOTH Negotiation and Approved BLOCK shipping in every confirmation mode (they
    // replace the removed DateProposed row in the ship-gate matrix — see PoConfirmationPolicy.AllowsShipping).
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

/// <summary>
/// R5 (TSD R5 Addendum §4.4 / §8) — lifecycle of a <c>proc.DeliverySchedule</c> row. Created with
/// <c>Approved</c> when a PO becomes shippable; Phase 2 may add further states (e.g. Shipped, Cancelled).
/// Persisted as the enum name (string) via HasConversion&lt;string&gt;, no DB CHECK — the C# enum is the
/// guard. APPEND-ONLY.
/// </summary>
public enum DeliveryScheduleStatus { Approved }

/// <summary>
/// ASN lifecycle. R5 (TSD R5 Addendum §4.6 / §10) adds PendingApproval (supplier sent for buyer review)
/// and Rejected (buyer rejected; returned to supplier for edit). Full order:
/// Draft → PendingApproval → Submitted → InTransit → Delivered.
/// PendingApproval → Rejected → (supplier edits) → Draft.
/// Any active state → Cancelled.
/// Persisted as the enum name (string), no DB CHECK — the C# enum is the guard. APPEND-ONLY.
/// </summary>
public enum AsnStatus { Draft, Submitted, InTransit, Delivered, Cancelled, PendingApproval, Rejected }

/// <summary>
/// R5 (TSD R5 Addendum §4.6 / Component 6) — lifecycle of a single <c>proc.AsnApproval</c> session.
/// Created at Send-for-Approval with <c>Pending</c>; any mapped buyer advances it to <c>Approved</c>
/// (ASN → Submitted) or <c>Rejected</c> (ASN → Rejected, reason mandatory).
/// Persisted as the enum name (string) via HasConversion&lt;string&gt;, no DB CHECK — the C# enum is the
/// guard. APPEND-ONLY.
/// </summary>
public enum AsnApprovalStatus { Pending, Approved, Rejected }

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

/// <summary>
/// R6 (2026-07-02) — invoice provenance: <c>SupplierManual</c> = entered via the manual wizard;
/// <c>AsnGenerated</c> = auto-drafted by the grouped ASN generator at ASN approval. Persisted as the enum
/// name (string), no DB CHECK — the C# enum is the guard. APPEND-ONLY.
/// </summary>
public enum InvoiceOrigin { SupplierManual, AsnGenerated }

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
/// R4 (2026-06-26) — TSD R4 Addendum §3.4 / D1, Component 3 (PO Confirmation Gate). Per-supplier confirmation
/// mode that decides which PO state unblocks shipping (the ship-gate matrix, §6.2). REPLACES the old
/// <c>PoResponseMode {Manual,Auto}</c> (column kept as <c>poResponseMode</c>; 2b only data-migrates the stored
/// values Manual→AcceptToShip / Auto→AutoAccept). Persisted as the enum name (string), no DB CHECK — the C# enum
/// is the guard.
/// <list type="bullet">
///   <item><c>AutoAccept</c> — the portal auto-stamps Accepted + acceptedAt at PO release (ship-gate open at
///         Released); retains the old <c>Auto</c> behaviour. No manual confirmation step.</item>
///   <item><c>AcknowledgeToShip</c> — Acknowledged (or Accepted) unblocks shipping.</item>
///   <item><c>AcceptToShip</c> (default) — the PO must be Accepted before shipping.</item>
/// </list>
/// APPEND-ONLY.
/// </summary>
public enum PoConfirmationMode { AutoAccept, AcknowledgeToShip, AcceptToShip }

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

// R8 (2026-07-04) — IDM document outbox (TSD R8). Persisted as the enum NAME (string) with a matching
// DB CHECK constraint. Operation = the IDM item mutation; Status = the per-document-partition dispatch state.
public enum IdmOutboxOperation { Create, Update, Delete }

/// <summary>
/// R8 — IDM document outbox row state (per-<c>documentUploadId</c> FIFO partition):
/// <c>Blocked</c> (gate unsatisfied) → <c>Pending</c> (eligible) → <c>InFlight</c> (claimed) → <c>Success</c>;
/// transient failure returns to <c>Pending</c> with backoff, exhausted/validation failure → <c>Failed</c>;
/// a terminal parent condition moves a still-<c>Blocked</c> row to <c>Unresolvable</c>.
/// </summary>
public enum IdmOutboxStatus { Blocked, Pending, InFlight, Success, Failed, Unresolvable }

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

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.8, Component 5 (Attachment Requirement Governance). The strength at
/// which an attachment type is required when a transaction (ASN / Invoice / Supplier) is submitted:
/// <c>Mandatory</c> = submit blocked until present; <c>Warning</c> = skippable after explicit confirmation
/// (audited); <c>Optional</c> = skippable silently (also the default when no policy row exists). Persisted as
/// the enum name (string), no DB CHECK — the C# enum is the guard. APPEND-ONLY.
/// </summary>
public enum AttachmentRequirement { Mandatory, Warning, Optional }
