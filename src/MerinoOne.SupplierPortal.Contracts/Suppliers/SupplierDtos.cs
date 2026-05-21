namespace MerinoOne.SupplierPortal.Contracts.Suppliers;

public record SupplierListItemDto(
    Guid Id,
    int Seq,
    string SupplierCode,
    string LegalName,
    string? TradeName,
    string? GstNumber,
    string? PanNumber,
    string RegistrationStatus,
    bool IsActiveSupplier,
    DateTime CreatedOn);

public record SupplierDetailDto(
    Guid Id,
    int Seq,
    string SupplierCode,
    string LegalName,
    string? TradeName,
    string SupplierType,
    string? GstNumber,
    string? PanNumber,
    string? MsmeRegNumber,
    string? MsmeCategory,
    bool GstValidated,
    bool PanValidated,
    bool MsmeValidated,
    string RegistrationStatus,
    string? InvitedBy,
    DateTime? InvitedAt,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? ApprovalOverrideComment,
    string? RejectionReason,
    string? Website,
    bool IsActiveSupplier,
    List<SupplierVerificationDto> Verifications);

public record SupplierVerificationDto(
    Guid Id,
    string VerificationType,
    DateTime AttemptedAt,
    string AttemptedBy,
    string ProviderName,
    string Result,
    string? Comments);

public record InviteSupplierRequest(string LegalName, string Email, string SupplierType);
public record ApproveSupplierRequest(string? OverrideComment);
public record RejectSupplierRequest(string Reason);
public record VerifyNicRequest(string[] Types);
