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
    string Status); // Pending | Consumed | Expired

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
    string Status);

public record CreateSupplierInviteRequest(string LegalName, string Email, string? MobileNo = null);

public record CreateSupplierInviteResponse(
    SupplierInviteDetailDto Invite,
    string Token,
    string RegistrationUrl);

public record SupplierAddressInput(
    string AddressType,
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    string? Country);

public record SupplierContactInput(
    string Name,
    string? Designation,
    string Email,
    string? Phone,
    bool IsPrimary);

/// <summary>
/// One uploaded document captured during self-registration. DocumentType is the
/// string name of <see cref="MerinoOne.SupplierPortal.Domain.Enums.DocumentType"/>
/// (e.g. "OnboardingPan", "OnboardingGst", "OnboardingCheque", "OnboardingMsme").
/// FileUrl is the storage path/URL produced by the upload endpoint (mocked in Stage 1).
/// </summary>
public record UploadedDocumentInput(
    string DocumentType,
    string FileName,
    string FileUrl,
    long FileSizeKb,
    string MimeType);

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
    List<UploadedDocumentInput> Documents);

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
