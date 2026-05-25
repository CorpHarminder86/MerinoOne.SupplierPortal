using System.Net;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Admin;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.Admin.EmailTemplates;

/// <summary>
/// Renders an admin-supplied subject + body with a fixed dummy-placeholder dictionary covering
/// every known placeholder key, then dispatches via <see cref="IEmailService.SendAsync"/>.
/// The recipient is provided by the admin; the subject is prefixed with <c>[TEST]</c> so the
/// inbox makes it obvious this isn't a production email. Returns a structured result instead
/// of throwing so the admin UI can surface SMTP errors verbatim.
/// </summary>
public record SendTestEmailTemplateCommand(SendTestEmailTemplateRequest Body) : IRequest<TestEmailResult>;

public class SendTestEmailTemplateCommandValidator : AbstractValidator<SendTestEmailTemplateCommand>
{
    public SendTestEmailTemplateCommandValidator()
    {
        RuleFor(x => x.Body.ToEmail).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Body.Subject).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.HtmlBody).NotEmpty().MaximumLength(100_000);
    }
}

public class SendTestEmailTemplateCommandHandler
    : IRequestHandler<SendTestEmailTemplateCommand, TestEmailResult>
{
    // Same regex used by EmailTemplateRenderer — keep the two in lock-step.
    private static readonly Regex PlaceholderRegex = new(
        @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Dummy values cover EVERY placeholder ever emitted by the seeder — extending the seeder
    // will require extending this map too.
    private static readonly IReadOnlyDictionary<string, string?> DummyPlaceholders =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = "test.user@example.com",
            ["fullName"] = "Test User",
            ["legalName"] = "Acme Suppliers Pty Ltd",
            ["userCode"] = "USR-0001",
            ["oneTimePassword"] = "TempPass#1",
            ["loginUrl"] = "https://supplierportal.example.com/login",
            ["registrationUrl"] = "https://supplierportal.example.com/register/abc123token",
            ["mobileNo"] = "+919876543210",
            ["expiresAt"] = DateTime.UtcNow.AddDays(7).ToString("u"),
            ["otp"] = "123456",
            ["validMinutes"] = "10",
            ["changedAtUtc"] = DateTime.UtcNow.ToString("u") + " UTC",
        };

    private readonly IEmailService _email;
    private readonly ILogger<SendTestEmailTemplateCommandHandler> _logger;

    public SendTestEmailTemplateCommandHandler(
        IEmailService email,
        ILogger<SendTestEmailTemplateCommandHandler> logger)
    {
        _email = email;
        _logger = logger;
    }

    public async Task<TestEmailResult> Handle(SendTestEmailTemplateCommand request, CancellationToken ct)
    {
        var toEmail = request.Body.ToEmail.Trim();
        var subject = "[TEST] " + Substitute(request.Body.Subject, htmlEncode: false);
        var htmlBody = Substitute(request.Body.HtmlBody, htmlEncode: true);

        try
        {
            await _email.SendAsync(toEmail, subject, htmlBody, ct);
            return new TestEmailResult(true, $"Test email dispatched to {toEmail}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test template email failed for {Recipient}.", toEmail);
            return new TestEmailResult(false, ex.Message);
        }
    }

    private static string Substitute(string template, bool htmlEncode)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
        return PlaceholderRegex.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (!DummyPlaceholders.TryGetValue(name, out var raw)) return match.Value;
            var value = raw ?? string.Empty;
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }
}
