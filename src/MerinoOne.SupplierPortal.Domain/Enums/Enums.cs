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
    Cancelled
}

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

/// <summary>
/// Transactional-outbox row lifecycle for the post-commit ERP dispatch helper (Increment 0). Persisted as the
/// enum name (string), no CHECK — the C# enum is the guard (matches the dominant status-enum convention).
/// APPEND-ONLY.
/// </summary>
public enum OutboxStatus { Pending, Dispatched, Acked, Failed }

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

/// <summary>Unit dimension / quantity type (INFOR LN unit type).</summary>
public enum UnitType { Quantity, Length, Area, Volume, Mass, Time, Other }
