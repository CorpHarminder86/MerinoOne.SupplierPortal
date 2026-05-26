using System.Net;
using System.Net.Mail;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Live SMTP transport. Mirrors the settings access pattern proven by
/// <c>SendTestEmailCommand</c> (Host/Port/EnableSsl/UseDefaultCredentials + optional
/// NetworkCredential + From-address). Body templates are duplicated verbatim from
/// <see cref="MockEmailService"/> so QA logs and real inboxes render identically.
/// </summary>
internal sealed class SmtpEmailService : IEmailService
{
    private readonly IEmailConfig _config;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IEmailConfig config, ILogger<SmtpEmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (!_config.IsConfigured)
        {
            var msg = "Email config is incomplete — set Host and Email (from-address) before sending.";
            _logger.LogWarning("SMTP send aborted (not configured). TO={Recipient} SUBJECT={Subject}", toEmail, subject);
            throw new InvalidOperationException(msg);
        }

        try
        {
            using var client = new SmtpClient(_config.Host, _config.Port)
            {
                EnableSsl = _config.EnableSsl,
                UseDefaultCredentials = _config.DefaultCredentials,
                // Cap socket+handshake wait so API responds before the Web client's 30s cancel.
                // Default would be 100,000 ms (100s) — too long for an interactive "Send test" call.
                Timeout = 15_000,
            };

            if (!_config.DefaultCredentials)
            {
                client.Credentials = new NetworkCredential(_config.UserName, _config.Password);
            }

            using var msg = new MailMessage
            {
                From = new MailAddress(_config.FromAddress),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            msg.To.Add(toEmail);

            await client.SendMailAsync(msg, ct);
            _logger.LogInformation("[SmtpEmail] Sent TO={Recipient} SUBJECT={Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SMTP send failed. TO={Recipient} SUBJECT={Subject} HOST={Host} PORT={Port}",
                toEmail, subject, _config.Host, _config.Port);
            throw;
        }
    }

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
        return SendAsync(toEmail, subject, body, ct);
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

    // ─── Body builders (mirror MockEmailService verbatim) ──────────────────────

    private static string BuildWelcomeBody(
        string toEmail, string fullName, string userCode, string oneTimePassword, string loginUrl)
    {
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
