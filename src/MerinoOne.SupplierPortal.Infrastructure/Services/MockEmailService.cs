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
}
