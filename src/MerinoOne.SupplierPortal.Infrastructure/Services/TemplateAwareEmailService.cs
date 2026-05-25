using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Decorator that wraps the underlying <see cref="IEmailService"/> (Mock or Smtp)
/// and consults <see cref="IEmailTemplateRenderer"/> first. When an active admin-editable
/// template exists for the matching key, the rendered subject + body are dispatched via the
/// generic <see cref="IEmailService.SendAsync"/> path. When no template exists (or none is
/// active), the call is forwarded to the inner service so the existing hardcoded body fires.
/// The placeholder names are kept in lock-step with what the seeder wrote into the default
/// bodies — <c>fullName</c>, <c>userCode</c>, <c>oneTimePassword</c>, <c>loginUrl</c>,
/// <c>legalName</c>, <c>mobileNo</c>, <c>registrationUrl</c>, <c>expiresAt</c>, <c>otp</c>,
/// <c>validMinutes</c>, <c>changedAtUtc</c>.
/// </summary>
internal sealed class TemplateAwareEmailService : IEmailService
{
    private readonly IEmailService _inner;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly ILogger<TemplateAwareEmailService> _logger;

    public TemplateAwareEmailService(
        IEmailService inner,
        IEmailTemplateRenderer renderer,
        ILogger<TemplateAwareEmailService> logger)
    {
        _inner = inner;
        _renderer = renderer;
        _logger = logger;
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        => _inner.SendAsync(toEmail, subject, htmlBody, ct);

    public async Task SendWelcomeEmailAsync(
        string toEmail,
        string fullName,
        string userCode,
        string oneTimePassword,
        string loginUrl,
        CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["fullName"] = fullName,
            ["userCode"] = userCode,
            ["oneTimePassword"] = oneTimePassword,
            ["loginUrl"] = loginUrl,
        };

        var rendered = await _renderer.TryRenderAsync("Welcome", placeholders, ct);
        if (rendered is not null)
        {
            await _inner.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            return;
        }

        await _inner.SendWelcomeEmailAsync(toEmail, fullName, userCode, oneTimePassword, loginUrl, ct);
    }

    public async Task SendInviteEmailAsync(
        string toEmail,
        string legalName,
        string? mobileNo,
        string registrationUrl,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["legalName"] = legalName,
            ["mobileNo"] = mobileNo,
            ["registrationUrl"] = registrationUrl,
            ["expiresAt"] = expiresAt.ToString("u"),
        };

        var rendered = await _renderer.TryRenderAsync("Invite", placeholders, ct);
        if (rendered is not null)
        {
            await _inner.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            return;
        }

        await _inner.SendInviteEmailAsync(toEmail, legalName, mobileNo, registrationUrl, expiresAt, ct);
    }

    public async Task SendPasswordChangedAsync(
        string toEmail,
        string fullName,
        string changedAtUtc,
        CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["fullName"] = fullName,
            ["changedAtUtc"] = changedAtUtc,
        };

        var rendered = await _renderer.TryRenderAsync("PasswordChanged", placeholders, ct);
        if (rendered is not null)
        {
            await _inner.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            return;
        }

        await _inner.SendPasswordChangedAsync(toEmail, fullName, changedAtUtc, ct);
    }

    public async Task SendInviteOtpAsync(
        string toEmail,
        string legalName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["legalName"] = legalName,
            ["otp"] = otp,
            ["validMinutes"] = validMinutes.ToString(),
        };

        var rendered = await _renderer.TryRenderAsync("InviteOtp", placeholders, ct);
        if (rendered is not null)
        {
            await _inner.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            return;
        }

        await _inner.SendInviteOtpAsync(toEmail, legalName, otp, validMinutes, ct);
    }

    public async Task SendLoginOtpAsync(
        string toEmail,
        string fullName,
        string otp,
        int validMinutes,
        CancellationToken ct = default)
    {
        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["fullName"] = fullName,
            ["otp"] = otp,
            ["validMinutes"] = validMinutes.ToString(),
        };

        var rendered = await _renderer.TryRenderAsync("LoginOtp", placeholders, ct);
        if (rendered is not null)
        {
            await _inner.SendAsync(toEmail, rendered.Subject, rendered.HtmlBody, ct);
            return;
        }

        await _inner.SendLoginOtpAsync(toEmail, fullName, otp, validMinutes, ct);
    }
}
