namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Stage 1 transactional email gateway. The mock implementation writes to Serilog
/// and to logs/emails-YYYYMMDD.log so QA can fish out OTPs without an SMTP server.
/// </summary>
public interface IEmailService
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);

    Task SendWelcomeEmailAsync(
        string toEmail,
        string fullName,
        string userCode,
        string oneTimePassword,
        string loginUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Sends the registration invite email to a prospective supplier. The link
    /// <paramref name="registrationUrl"/> must already include the invite token.
    /// </summary>
    Task SendInviteEmailAsync(
        string toEmail,
        string legalName,
        string? mobileNo,
        string registrationUrl,
        DateTime expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Security-event notification dispatched after a successful self-service password change.
    /// <paramref name="changedAtUtc"/> is a pre-formatted string so the caller can substitute a
    /// timezone-localised value when available.
    /// </summary>
    Task SendPasswordChangedAsync(
        string toEmail,
        string fullName,
        string changedAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a 6-digit OTP to a prospective supplier so they can complete the invite
    /// landing-page verification step without clicking the registration link. The OTP
    /// is one-time use and expires after <paramref name="validMinutes"/>.
    /// </summary>
    Task SendInviteOtpAsync(
        string toEmail,
        string legalName,
        string otp,
        int validMinutes,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a 6-digit MFA verification code after the password leg of login succeeds
    /// for an MFA-enabled user. The OTP is one-time use and expires after
    /// <paramref name="validMinutes"/>.
    /// </summary>
    Task SendLoginOtpAsync(
        string toEmail,
        string fullName,
        string otp,
        int validMinutes,
        CancellationToken ct = default);
}
