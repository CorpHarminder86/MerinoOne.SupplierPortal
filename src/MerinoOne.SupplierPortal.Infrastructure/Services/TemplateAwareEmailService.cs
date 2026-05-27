using System.Net;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Public IEmailService used by all command handlers. Renders the requested template via
/// <see cref="IEmailTemplateRenderer"/>, writes a Pending row to <c>admin.emailOutbox</c>,
/// returns immediately. The EmailOutboxWorker BackgroundService drains rows and dispatches
/// through <see cref="IEmailSender"/>.
///
/// On missing/inactive template, falls back to inline hardcoded bodies (so seed regressions
/// don't silently drop emails) and STILL enqueues — the worker is the only thing that
/// touches SMTP. Handlers never wait for SMTP.
/// </summary>
internal sealed class TemplateAwareEmailService : IEmailService
{
    private readonly IAppDbContext _db;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly ILogger<TemplateAwareEmailService> _logger;

    public TemplateAwareEmailService(
        IAppDbContext db,
        IEmailTemplateRenderer renderer,
        ILogger<TemplateAwareEmailService> logger)
    {
        _db = db;
        _renderer = renderer;
        _logger = logger;
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        => EnqueueAsync("Raw", toEmail, subject, htmlBody, ct);

    public Task SendWelcomeEmailAsync(
        string toEmail,
        string fullName,
        string userCode,
        string oneTimePassword,
        string loginUrl,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "Welcome",
            toEmail,
            "Welcome to MerinoOne Supplier Portal",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["fullName"] = fullName,
                ["userCode"] = userCode,
                ["oneTimePassword"] = oneTimePassword,
                ["loginUrl"] = loginUrl,
            },
            fallbackBody: () => BuildFallbackWelcomeBody(toEmail, fullName, userCode, oneTimePassword, loginUrl),
            ct);

    public Task SendInviteEmailAsync(
        string toEmail,
        string legalName,
        string? mobileNo,
        string registrationUrl,
        DateTime expiresAt,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "Invite",
            toEmail,
            "You're invited to register on MerinoOne Supplier Portal",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["legalName"] = legalName,
                ["mobileNo"] = mobileNo,
                ["registrationUrl"] = registrationUrl,
                ["expiresAt"] = expiresAt.ToString("u"),
            },
            fallbackBody: () => BuildFallbackInviteBody(legalName, registrationUrl, expiresAt),
            ct);

    public Task SendPasswordChangedAsync(
        string toEmail,
        string fullName,
        string changedAtUtc,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "PasswordChanged",
            toEmail,
            "Your portal password was changed",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["fullName"] = fullName,
                ["changedAtUtc"] = changedAtUtc,
            },
            fallbackBody: () => $"<p>Hello {WebUtility.HtmlEncode(fullName)}, your portal password was changed at {WebUtility.HtmlEncode(changedAtUtc)}.</p>",
            ct);

    public Task SendInviteOtpAsync(
        string toEmail,
        string legalName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "InviteOtp",
            toEmail,
            "Your OTP to complete supplier registration",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["legalName"] = legalName,
                ["otp"] = otp,
                ["validMinutes"] = validMinutes.ToString(),
            },
            fallbackBody: () => BuildFallbackOtpBody(legalName, otp, validMinutes, "supplier registration"),
            ct);

    public Task SendLoginOtpAsync(
        string toEmail,
        string fullName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "LoginOtp",
            toEmail,
            "Your sign-in verification code",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["fullName"] = fullName,
                ["otp"] = otp,
                ["validMinutes"] = validMinutes.ToString(),
            },
            fallbackBody: () => BuildFallbackOtpBody(fullName, otp, validMinutes, "sign-in"),
            ct);

    public Task SendRegistrationAcknowledgementAsync(
        string toEmail,
        string legalName,
        string supplierCode,
        string contactEmail,
        string status,
        CancellationToken ct = default)
        => EnqueueWithTemplateAsync(
            "RegistrationAcknowledgement",
            toEmail,
            $"We received your supplier registration — {supplierCode}",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["toEmail"] = toEmail,
                ["legalName"] = legalName,
                ["supplierCode"] = supplierCode,
                ["contactEmail"] = contactEmail,
                ["status"] = status,
            },
            fallbackBody: () => $"<p>Thank you, {WebUtility.HtmlEncode(legalName)}. We have received your supplier registration ({WebUtility.HtmlEncode(supplierCode)}). Status: {WebUtility.HtmlEncode(status)}.</p>",
            ct);

    // ─── Internals ──────────────────────────────────────────────

    private async Task EnqueueWithTemplateAsync(
        string templateKey,
        string toEmail,
        string fallbackSubject,
        IReadOnlyDictionary<string, string?> placeholders,
        Func<string> fallbackBody,
        CancellationToken ct)
    {
        string subject;
        string body;

        var rendered = await _renderer.TryRenderAsync(templateKey, placeholders, ct);
        if (rendered is not null)
        {
            subject = rendered.Subject;
            body = rendered.HtmlBody;
        }
        else
        {
            _logger.LogWarning(
                "Email template {Key} missing/inactive — enqueueing fallback body. TO={Recipient}",
                templateKey, toEmail);
            subject = fallbackSubject;
            body = fallbackBody();
        }

        await EnqueueAsync(templateKey, toEmail, subject, body, ct);
    }

    private async Task EnqueueAsync(string templateKey, string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _db.EmailOutbox.Add(new EmailOutbox
        {
            Id = Guid.NewGuid(),
            TemplateKey = templateKey,
            ToEmail = toEmail,
            Subject = subject,
            HtmlBody = htmlBody,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedBy = "enqueue",
            CreatedOn = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    // ─── Inline fallback bodies (used only when template seed regresses) ────────

    private static string BuildFallbackWelcomeBody(string toEmail, string fullName, string userCode, string oneTimePassword, string loginUrl) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">MerinoOne Supplier Portal — Welcome, {WebUtility.HtmlEncode(fullName)}</h2>
  <p>Your supplier registration has been approved.</p>
  <table cellpadding="6" style="border-collapse:collapse;border:1px solid #e5e7eb;">
    <tr><td><b>Login URL</b></td><td><a href="{loginUrl}">{loginUrl}</a></td></tr>
    <tr><td><b>Email</b></td><td>{WebUtility.HtmlEncode(toEmail)}</td></tr>
    <tr><td><b>User code</b></td><td>{WebUtility.HtmlEncode(userCode)}</td></tr>
    <tr><td><b>One-time password</b></td><td><code style="background:#fef3c7;padding:2px 6px;">{WebUtility.HtmlEncode(oneTimePassword)}</code></td></tr>
  </table>
</body></html>
""";

    private static string BuildFallbackInviteBody(string legalName, string registrationUrl, DateTime expiresAt) => $"""
<!DOCTYPE html>
<html><body>
  <h2>You're invited to register on MerinoOne Supplier Portal</h2>
  <p>Hello {WebUtility.HtmlEncode(legalName)},</p>
  <p><a href="{registrationUrl}">{registrationUrl}</a></p>
  <p>Expires {expiresAt:u} (UTC).</p>
</body></html>
""";

    private static string BuildFallbackOtpBody(string name, string otp, int validMinutes, string purpose) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">MerinoOne Supplier Portal — Verification code</h2>
  <p>Hello {WebUtility.HtmlEncode(name)},</p>
  <p>Use the verification code below for {purpose}:</p>
  <p style="font-size:28px;font-weight:700;letter-spacing:6px;background:#fef3c7;padding:12px 20px;display:inline-block;border-radius:6px;">
    <code>{WebUtility.HtmlEncode(otp)}</code>
  </p>
  <p>Valid for <b>{validMinutes} minutes</b>.</p>
</body></html>
""";
}
