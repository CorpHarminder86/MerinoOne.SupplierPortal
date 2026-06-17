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
    List<SupplierVerificationDto> Verifications,
    List<SupplierAddressDto> Addresses,
    List<SupplierContactDto> Contacts,
    List<SupplierDocumentDto> Documents,
    SupplierInviteSummaryDto? InviteSummary,
    List<SupplierUserDto> LinkedUsers);

/// <summary>
/// A portal user mapped to this supplier (via SupplierUserMap → SecRight). Resolved cross-company /
/// filter-bypassed for the admin supplier-detail view so every linked user shows regardless of the header's
/// active company. <see cref="CanWrite"/> is the mapping's SecRight access level.
/// </summary>
public record SupplierUserDto(
    Guid UserId,
    string UserCode,
    string FullName,
    string Email,
    bool IsInternal,
    bool IsActive,
    bool CanWrite);

public record SupplierVerificationDto(
    Guid Id,
    string VerificationType,
    DateTime AttemptedAt,
    string AttemptedBy,
    string ProviderName,
    string Result,
    string? Comments);

public record SupplierAddressDto(
    Guid Id,
    string AddressType,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string Pincode,
    string Country);

public record SupplierContactDto(
    Guid Id,
    string ContactName,
    string? Designation,
    string Email,
    string? Phone,
    bool IsPrimary);

public record SupplierDocumentDto(
    Guid Id,
    string DocumentType,
    string FileName,
    long FileSizeKb,
    string MimeType,
    string AiValidationStatus,
    decimal? AiValidationConfidence,
    DateTime? UploadedAt,
    string FileUrl); // GET /api/document-uploads/{id}

public record SupplierInviteSummaryDto(
    Guid Id,
    string LegalName,
    string Email,
    string? MobileNo,
    string InvitedBy,
    DateTime InvitedAt,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    DateTime? CancelledAt,
    DateTime? LastResentAt,
    int ResendCount,
    string Status);

public record InviteSupplierRequest(string LegalName, string Email, string SupplierType);
public record ApproveSupplierRequest(string? OverrideComment);
public record RejectSupplierRequest(string Reason);
public record VerifyNicRequest(string[] Types);
