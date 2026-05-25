using System.Net;
using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Stage 1 email transport — no SMTP. Every send is logged at Information level
/// AND appended to a daily file (<c>logs/emails-YYYYMMDD.log</c>) so the
/// onboarding OTP is recoverable during local/dev smoke tests.
/// </summary>
public class MockEmailService : IEmailService
{
    private readonly ILogger<MockEmailService> _logger;
    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    public MockEmailService(ILogger<MockEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        => WriteAsync(toEmail, subject, htmlBody, ct);

    public Task SendWelcomeEmailAsync(
        string toEmail,
        string fullName,
        string userCode,
        string oneTimePassword,
        string loginUrl,
        CancellationToken ct = default)
    {
        var subject = "Welcome to MerinoOne Supplier Portal";
        var body = BuildWelcomeBody(toEmail, fullName, userCode, oneTimePassword, loginUrl);
        return WriteAsync(toEmail, subject, body, ct);
    }

    public Task SendInviteEmailAsync(
        string toEmail,
        string legalName,
        string? mobileNo,
        string registrationUrl,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        var subject = "You're invited to register on MerinoOne Supplier Portal";
        var body = BuildInviteBody(legalName, mobileNo, registrationUrl, expiresAt);
        return SendAsync(toEmail, subject, body, ct);
    }

    public Task SendPasswordChangedAsync(
        string toEmail,
        string fullName,
        string changedAtUtc,
        CancellationToken ct = default)
    {
        var subject = "Your portal password was changed";
        var body = BuildPasswordChangedBody(fullName, changedAtUtc);
        return SendAsync(toEmail, subject, body, ct);
    }

    public Task SendInviteOtpAsync(
        string toEmail,
        string legalName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
    {
        var subject = "Your OTP to complete supplier registration";
        var body = BuildInviteOtpBody(legalName, otp, validMinutes);
        return SendAsync(toEmail, subject, body, ct);
    }

    public Task SendLoginOtpAsync(
        string toEmail,
        string fullName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
    {
        var subject = "Your sign-in verification code";
        var body = BuildLoginOtpBody(fullName, otp, validMinutes);
        return SendAsync(toEmail, subject, body, ct);
    }

    private async Task WriteAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow;
        _logger.LogInformation(
            "[MockEmail] TO={Recipient} SUBJECT={Subject} BODY_LEN={BodyLen}",
            toEmail, subject, htmlBody?.Length ?? 0);

        var line = new StringBuilder()
            .Append('[').Append(timestamp.ToString("o")).Append("] ")
            .Append("TO:").Append(toEmail).Append(' ')
            .Append("SUBJECT:").Append(subject).Append('\n')
            .Append(htmlBody ?? string.Empty).Append('\n')
            .Append("---").Append('\n')
            .ToString();

        var dir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"emails-{timestamp:yyyyMMdd}.log");

        await _fileLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(file, line, Encoding.UTF8, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string BuildWelcomeBody(
        string toEmail, string fullName, string userCode, string oneTimePassword, string loginUrl)
    {
        // Plain-ish HTML; the file log doubles as the "inbox" for QA so keep it readable.
        return $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">MerinoOne Supplier Portal — Welcome, {fullName}</h2>
  <p>Your supplier registration has been approved. A portal account has been provisioned for you.</p>
  <table cellpadding="6" style="border-collapse:collapse;border:1px solid #e5e7eb;">
    <tr><td><b>Login URL</b></td><td><a href="{loginUrl}">{loginUrl}</a></td></tr>
    <tr><td><b>Email (sign in)</b></td><td>{toEmail}</td></tr>
    <tr><td><b>User code</b></td><td>{userCode}</td></tr>
    <tr><td><b>One-time password</b></td><td><code style="background:#fef3c7;padding:2px 6px;">{oneTimePassword}</code></td></tr>
  </table>
  <p style="color:#b91c1c;"><b>Important:</b> You will be required to change this password on your first sign-in.</p>
  <p>If you did not expect this email, please contact your MerinoOne purchasing contact.</p>
  <hr/>
  <p style="font-size:12px;color:#6b7280;">&copy; Merino Consulting Services Ltd.</p>
</body></html>
""";
    }

    private static string BuildInviteBody(string legalName, string? mobileNo, string registrationUrl, DateTime expiresAt)
    {
        // legalName is user-controlled (entered by an admin); encode to prevent any HTML breaking out
        // of the greeting. mobileNo and registrationUrl are emitted as-is — mobileNo is validated to
        // digits/+ by FluentValidation, and registrationUrl is built server-side from Web:BaseUrl.
        var safeLegalName = WebUtility.HtmlEncode(legalName ?? string.Empty);
        var expires = expiresAt.ToString("u");
        var mobileLine = string.IsNullOrWhiteSpace(mobileNo)
            ? string.Empty
            : $"<p>We have your mobile number on file ({WebUtility.HtmlEncode(mobileNo)}). We will also use your number for OTP verification at sign-up.</p>";

        return $"""
<!DOCTYPE html>
<html><body>
  <h2>You're invited to register on MerinoOne Supplier Portal</h2>
  <p>Hello {safeLegalName},</p>
  <p>You have been invited to register as a supplier on the MerinoOne Supplier Portal.</p>
  <p>To complete your registration, click the link below:</p>
  <p><a href="{registrationUrl}">{registrationUrl}</a></p>
  <p>This invitation expires on {expires} (UTC).</p>
  {mobileLine}
  <p>If you did not expect this email, please ignore it.</p>
  <hr/>
  <p>&copy; Merino Consulting Services Ltd.</p>
</body></html>
""";
    }

    private static string BuildPasswordChangedBody(string fullName, string changedAtUtc)
    {
        var safeFullName = WebUtility.HtmlEncode(fullName ?? string.Empty);
        var safeWhen = WebUtility.HtmlEncode(changedAtUtc ?? string.Empty);
        return $"""
<!DOCTYPE html>
<html><body>
  <h2>Your portal password was changed</h2>
  <p>Hello {safeFullName},</p>
  <p>Your portal password was changed at {safeWhen}. If this wasn't you, contact support immediately.</p>
  <hr/>
  <p>&copy; Merino Consulting Services Ltd.</p>
</body></html>
""";
    }

    private static string BuildInviteOtpBody(string legalName, string otp, int validMinutes)
    {
        // legalName is admin-entered; encode it. otp is generated server-side from RNG so it is
        // already safe — but still emit inside <code> so any styling stays contained.
        var safeLegalName = WebUtility.HtmlEncode(legalName ?? string.Empty);
        var safeOtp = WebUtility.HtmlEncode(otp ?? string.Empty);
        return $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">MerinoOne Supplier Portal — Verification code</h2>
  <p>Hello {safeLegalName},</p>
  <p>Use the verification code below to complete your supplier registration on the MerinoOne Supplier Portal:</p>
  <p style="font-size:28px;font-weight:700;letter-spacing:6px;background:#fef3c7;padding:12px 20px;display:inline-block;border-radius:6px;">
    <code>{safeOtp}</code>
  </p>
  <p>This code is valid for <b>{validMinutes} minutes</b>.</p>
  <p style="color:#b91c1c;"><b>Do not share this code</b> with anyone. MerinoOne staff will never ask you for this code.</p>
  <p>If you did not request this code, please ignore this email.</p>
  <hr/>
  <p style="font-size:12px;color:#6b7280;">&copy; Merino Consulting Services Ltd.</p>
</body></html>
""";
    }

    private static string BuildLoginOtpBody(string fullName, string otp, int validMinutes)
    {
        var safeFullName = WebUtility.HtmlEncode(fullName ?? string.Empty);
        var safeOtp = WebUtility.HtmlEncode(otp ?? string.Empty);
        return $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">MerinoOne Supplier Portal — Sign-in verification</h2>
  <p>Hello {safeFullName},</p>
  <p>Use the verification code below to complete your sign-in:</p>
  <p style="font-size:28px;font-weight:700;letter-spacing:6px;background:#fef3c7;padding:12px 20px;display:inline-block;border-radius:6px;">
    <code>{safeOtp}</code>
  </p>
  <p>This code is valid for <b>{validMinutes} minutes</b>.</p>
  <p style="color:#b91c1c;"><b>Do not share this code</b> with anyone. MerinoOne staff will never ask you for this code.</p>
  <p>If you did not attempt to sign in, please reset your password immediately and contact support.</p>
  <hr/>
  <p style="font-size:12px;color:#6b7280;">&copy; Merino Consulting Services Ltd.</p>
</body></html>
""";
    }
}
