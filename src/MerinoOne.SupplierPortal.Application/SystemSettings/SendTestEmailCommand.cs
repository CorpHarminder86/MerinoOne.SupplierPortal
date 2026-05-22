using System.Net;
using System.Net.Mail;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings;

/// <summary>
/// Sends a one-off SMTP test message using the saved <see cref="IEmailConfig"/> values.
/// Returns a structured <see cref="TestEmailResult"/> instead of throwing — the UI surfaces
/// the message verbatim so the operator can diagnose connection/auth failures inline.
/// </summary>
public record SendTestEmailCommand(string? ToEmail) : IRequest<TestEmailResult>;

public class SendTestEmailCommandHandler : IRequestHandler<SendTestEmailCommand, TestEmailResult>
{
    private readonly IEmailConfig _config;
    private readonly ICurrentUser _user;
    private readonly IAppDbContext _db;
    private readonly ILogger<SendTestEmailCommandHandler> _logger;

    public SendTestEmailCommandHandler(
        IEmailConfig config,
        ICurrentUser user,
        IAppDbContext db,
        ILogger<SendTestEmailCommandHandler> logger)
    {
        _config = config;
        _user = user;
        _db = db;
        _logger = logger;
    }

    public async Task<TestEmailResult> Handle(SendTestEmailCommand request, CancellationToken ct)
    {
        if (!_config.IsConfigured)
            return new TestEmailResult(false,
                "Email config is incomplete — set Host and Email (from-address) before sending.");

        // Resolve recipient: explicit override → current user's email (looked up by UserCode).
        var to = (request.ToEmail ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(to))
        {
            var code = _user?.UserCode ?? string.Empty;
            if (!string.IsNullOrEmpty(code))
            {
                to = await _db.AppUsers
                    .Where(u => u.UserCode == code)
                    .Select(u => u.Email)
                    .FirstOrDefaultAsync(ct) ?? string.Empty;
            }
        }
        if (string.IsNullOrWhiteSpace(to))
            return new TestEmailResult(false,
                "No recipient supplied and the current user has no email on record.");

        try
        {
            using var client = new SmtpClient(_config.Host, _config.Port)
            {
                EnableSsl = _config.EnableSsl,
                UseDefaultCredentials = _config.DefaultCredentials,
            };

            if (!_config.DefaultCredentials)
            {
                client.Credentials = new NetworkCredential(_config.UserName, _config.Password);
            }

            using var msg = new MailMessage
            {
                From = new MailAddress(_config.FromAddress),
                Subject = "MerinoOne Supplier Portal — SMTP test",
                Body = $"This is a test message dispatched from the Supplier Portal at {DateTime.UtcNow:O} UTC.",
                IsBodyHtml = false,
            };
            msg.To.Add(to);

            await client.SendMailAsync(msg, ct);
            return new TestEmailResult(true, $"Test email dispatched to {to}.");
        }
        catch (SmtpException smtpEx)
        {
            _logger.LogWarning(smtpEx, "Test email failed (SMTP).");
            return new TestEmailResult(false, $"SMTP error: {smtpEx.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test email failed.");
            return new TestEmailResult(false, ex.Message);
        }
    }
}
