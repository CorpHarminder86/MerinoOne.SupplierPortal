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
    OnboardingMsme
}

public enum SyncDirection { Inbound, Outbound, Bidirectional }
public enum SyncStatus { Pending, Success, Failed, Retrying, Reconciled }
