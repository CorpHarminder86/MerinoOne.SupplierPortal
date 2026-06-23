namespace MerinoOne.SupplierPortal.Contracts.SupplierRegistration;

public record SupplierInviteListDto(
    Guid Id,
    int Seq,
    string LegalName,
    string Email,
    string InvitedBy,
    DateTime InvitedAt,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    Guid? SupplierId,
    string Token,
    string Status, // Pending | Consumed | Expired | Cancelled
    DateTime? CancelledAt = null,
    DateTime? LastResentAt = null,
    int ResendCount = 0);

public record SupplierInviteDetailDto(
    Guid Id,
    int Seq,
    string LegalName,
    string Email,
    string InvitedBy,
    DateTime InvitedAt,
    DateTime ExpiresAt,
    DateTime? ConsumedAt,
    Guid? SupplierId,
    string Token,
    string Status,
    DateTime? CancelledAt = null,
    DateTime? LastResentAt = null,
    int ResendCount = 0);

/// <summary>
/// Create a supplier invite. <see cref="TenantEntityId"/> is the company the invited supplier will be
/// registered under — required (must exist in the inviting admin's tenant). RegisterSupplierCommand copies
/// it onto the created Supplier so the supplier inherits its company.
/// </summary>
public record CreateSupplierInviteRequest(string LegalName, string Email, Guid TenantEntityId, string? MobileNo = null);

public record CreateSupplierInviteResponse(
    SupplierInviteDetailDto Invite,
    string Token,
    string RegistrationUrl);

public record SupplierAddressInput(
    string AddressType,
    string Line1,
    string? Line2,
    string? Area,
    string City,
    string State,
    string PostalCode,
    string? Country,
    // Optional geo-master links captured via the address autocomplete (snapshot strings stay authoritative).
    Guid? CountryId = null,
    Guid? StateId = null,
    Guid? CityId = null,
    Guid? PostalCodeId = null);

// R4 (2026-06-23) — AddressIndex optionally links this contact to one of the addresses created in the SAME
// registration request — it is the 0-based index into SupplierRegistrationRequest.Addresses (resolved by list
// order). Out-of-range / null → contact has no address link. Trailing optional → existing callers stay valid.
public record SupplierContactInput(
    string Name,
    string? Designation,
    string Email,
    string? Phone,
    bool IsPrimary,
    int? AddressIndex = null);

/// <summary>
/// One license / certification the supplier self-declares at onboarding (R4 #1). Attachments are added later
/// on the supplier's License tab (post-approval); here we capture the declaration fields only.
/// </summary>
public record SupplierLicenseInput(
    string LicenseNumber,
    string LicenseType,
    DateOnly? IssueDate = null,
    DateOnly? ExpiryDate = null,
    string? Remarks = null,
    // Doc ids uploaded anonymously (POST api/document-uploads, DocumentType=License) for THIS license; the
    // register handler rebinds them from the PendingInvite owner onto the created SupplierLicense.
    List<Guid>? AttachmentIds = null);

/// <summary>
/// One uploaded document referenced from a registration submission. The document row is
/// created by <c>POST api/document-uploads</c> ahead of time; only its <c>Id</c> is sent
/// here so the registration handler can rewrite ownership to the new supplier. DocumentType
/// is the string name of <see cref="MerinoOne.SupplierPortal.Domain.Enums.DocumentType"/>.
/// </summary>
public record UploadedDocumentInput(
    Guid Id,
    string DocumentType,
    string FileName,
    string FileUrl,
    long FileSizeKb,
    string MimeType);

/// <summary>Wire shape returned by the anonymous upload endpoint.</summary>
public record UploadedDocumentDto(
    Guid Id,
    string DocumentType,
    string FileName,
    int FileSizeKb,
    string MimeType,
    string FileUrl);

public record SupplierRegistrationRequest(
    string Token,
    string LegalName,
    string? TradeName,
    string SupplierType,
    string? GstNumber,
    string? PanNumber,
    string? MsmeRegNumber,
    string? Website,
    List<SupplierAddressInput> Addresses,
    List<SupplierContactInput> Contacts,
    List<UploadedDocumentInput> Documents,
    // R4 #1 — supplier self-declared licenses at onboarding (optional). Trailing optional so existing callers
    // stay valid. Commercial terms (currency/payment/delivery) are set internally on SupplierDetail, not here.
    List<SupplierLicenseInput>? Licenses = null);

public record SupplierRegistrationResponse(
    Guid SupplierId,
    string SupplierCode,
    string Status,
    string Message);

/// <summary>
/// Body posted by the invite landing page to verify the 6-digit OTP that was
/// emailed alongside the invite link.
/// </summary>
public record VerifyInviteOtpRequest(string Code);

/// <summary>
/// Result of an invite-OTP verification attempt. <c>RemainingAttempts</c> is
/// clamped to 0 when the OTP is invalidated (5 wrong attempts) so the UI can
/// switch to "request a new code" without doing the math itself.
/// </summary>
public record VerifyInviteOtpResponse(bool Verified, string? Message, int RemainingAttempts);

/// <summary>
/// Result of an invite-OTP resend. <c>RetryAfterSeconds</c> is &gt; 0 when the
/// request was throttled — clients should display the remaining cooldown.
/// </summary>
public record ResendInviteOtpResponse(bool Sent, string? Message, int RetryAfterSeconds);
