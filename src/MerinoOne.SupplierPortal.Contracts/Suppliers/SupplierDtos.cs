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
    List<SupplierUserDto> LinkedUsers,
    // R4 Module 1 — bank/license collections + term/currency snapshots + PO-response mode + ERP code.
    List<SupplierBankDetailDto> BankDetails,
    List<SupplierLicenseDto> Licenses,
    Guid? CurrencyId,
    string? CurrencyCode,
    Guid? PaymentTermId,
    string? PaymentTermCode,
    Guid? DeliveryTermId,
    string? DeliveryTermCode,
    string PoResponseMode,
    string? ErpCode);

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
    string? Area,
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

// ============================================================================================
// R4 Module 1 — Supplier bank details + licenses (BaseAggregateRoot, seccode-protected).
// ============================================================================================

public record SupplierBankDetailDto(
    Guid Id,
    int Seq,
    Guid SupplierId,
    string BankName,
    string BankAddress,
    string AccountName,
    string AccountNumber,
    Guid CurrencyId,
    string? CurrencyCode,
    string IfscCode,
    string? SwiftCode,
    bool IsPrimary,
    string? ErpCode,
    DateTime CreatedOn);

public record AddSupplierBankDetailRequest(
    string BankName,
    string BankAddress,
    string AccountName,
    string AccountNumber,
    Guid CurrencyId,
    string IfscCode,
    string? SwiftCode = null,
    bool IsPrimary = false);

public record UpdateSupplierBankDetailRequest(
    string BankName,
    string BankAddress,
    string AccountName,
    string AccountNumber,
    Guid CurrencyId,
    string IfscCode,
    string? SwiftCode,
    bool IsPrimary);

public record SupplierLicenseDto(
    Guid Id,
    int Seq,
    Guid SupplierId,
    string LicenseNumber,
    string LicenseType,
    string? Remarks,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    string? ErpCode,
    DateTime CreatedOn,
    // R4 (2026-06-22): license file attachments via the polymorphic doc.DocumentUpload table
    // (ownerEntityType='SupplierLicense', ownerEntityId=licenseId, DocumentType.License).
    // Trailing optional positional — Add/Update handlers return an empty list (the row is just-saved,
    // attachments are listed/managed via the dedicated attachment endpoints); GetSupplierById populates it.
    IReadOnlyList<DocumentAttachmentDto>? Attachments = null);

public record AddSupplierLicenseRequest(
    string LicenseNumber,
    string LicenseType,
    string? Remarks = null,
    DateOnly? IssueDate = null,
    DateOnly? ExpiryDate = null,
    // R4: deferred-upload rebind. When the UI uploads attachments BEFORE the license is first saved it
    // stages them under ownerEntityType='Staging'/ownerEntityId=<StagingKey> (a client-generated draft GUID);
    // on create the handler re-points those rows to the new license in the SAME transaction.
    Guid? StagingKey = null,
    // Optional explicit allow-list of staged attachment ids to rebind (defence-in-depth; when null ALL rows
    // under the StagingKey owned by the caller are rebound).
    IReadOnlyList<Guid>? AttachmentIds = null);

public record UpdateSupplierLicenseRequest(
    string LicenseNumber,
    string LicenseType,
    string? Remarks,
    DateOnly? IssueDate,
    DateOnly? ExpiryDate,
    // R4: same staging-rebind contract as AddSupplierLicenseRequest — lets the edit dialog attach newly-staged
    // files and have them re-pointed to this license on save. Existing attachments are unaffected.
    Guid? StagingKey = null,
    IReadOnlyList<Guid>? AttachmentIds = null);

/// <summary>
/// A file attached to a supplier-owned aggregate (today: SupplierLicense) via the polymorphic
/// <c>doc.DocumentUpload</c> table. Shape mirrors <see cref="SupplierDocumentDto"/> plus an explicit
/// preview/download split: both URLs are base-href-relative (NO leading slash) so the browser resolves
/// them against the Web app's <c>&lt;base href&gt;</c> and the Web proxy injects bearer auth.
/// </summary>
public record DocumentAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime UploadedOn,
    string DownloadUrl,   // files/proxy/{id}
    string PreviewUrl);   // files/proxy/{id} (same endpoint; inline Content-Disposition)

/// <summary>Expiring-license projection for the dashboard reminder query (carries supplier identity).</summary>
public record SupplierLicenseExpiringDto(
    Guid Id,
    Guid SupplierId,
    string SupplierCode,
    string SupplierLegalName,
    string LicenseNumber,
    string LicenseType,
    DateOnly? ExpiryDate,
    int? DaysToExpiry);

/// <summary>Admin sets the supplier's PO-response behaviour (Manual / Auto).</summary>
public record SetPoResponseModeRequest(string PoResponseMode);
