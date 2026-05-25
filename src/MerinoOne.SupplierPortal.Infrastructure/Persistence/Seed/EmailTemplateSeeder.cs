using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// Inserts the 5 default email templates if missing. Idempotent — skips any row whose
/// <c>templateKey</c> is already present (alive OR tombstoned, to avoid resurrecting
/// soft-deleted rows on every seed run).
/// </summary>
public static class EmailTemplateSeeder
{
    public record TemplateSpec(string TemplateKey, string Subject, string HtmlBody, string Notes);

    public static readonly IReadOnlyList<TemplateSpec> Specs = new[]
    {
        new TemplateSpec(
            "Invite",
            "You're invited to register on MerinoOne Supplier Portal",
            "<h2>Welcome to MerinoOne Supplier Portal</h2>" +
            "<p>Hello {{legalName}},</p>" +
            "<p>You have been invited to register as a supplier. Click the link below to start.</p>" +
            "<p><a href=\"{{registrationUrl}}\">Open registration</a></p>" +
            "<p>This invitation expires on {{expiresAt}} (UTC).</p>",
            "Placeholders: {{legalName}}, {{mobileNo}}, {{registrationUrl}}, {{expiresAt}}"),

        new TemplateSpec(
            "InviteOtp",
            "Your OTP to complete supplier registration",
            "<h2>Verify your invitation</h2>" +
            "<p>Hello {{legalName}},</p>" +
            "<p>Your one-time code is <b style=\"font-size:20px;letter-spacing:4px;\">{{otp}}</b>. " +
            "It expires in {{validMinutes}} minutes. Do not share this code.</p>",
            "Placeholders: {{legalName}}, {{otp}}, {{validMinutes}}"),

        new TemplateSpec(
            "Welcome",
            "Welcome to MerinoOne Supplier Portal",
            "<h2>Welcome aboard, {{fullName}}</h2>" +
            "<p>Your supplier account has been approved. Sign in with the credentials below and update your password on first login.</p>" +
            "<p>User code: <b>{{userCode}}</b><br/>Temporary password: <b>{{oneTimePassword}}</b></p>" +
            "<p><a href=\"{{loginUrl}}\">Open the portal</a></p>",
            "Placeholders: {{fullName}}, {{userCode}}, {{oneTimePassword}}, {{loginUrl}}"),

        new TemplateSpec(
            "PasswordChanged",
            "Your portal password was changed",
            "<h2>Password changed</h2>" +
            "<p>Hello {{fullName}},</p>" +
            "<p>Your portal password was changed at {{changedAtUtc}}. If this wasn't you, contact support immediately.</p>",
            "Placeholders: {{fullName}}, {{changedAtUtc}}"),

        new TemplateSpec(
            "LoginOtp",
            "Your sign-in verification code",
            "<h2>Two-step verification</h2>" +
            "<p>Hello {{fullName}},</p>" +
            "<p>Your one-time code is <b style=\"font-size:20px;letter-spacing:4px;\">{{otp}}</b>. " +
            "It expires in {{validMinutes}} minutes. Do not share this code.</p>",
            "Placeholders: {{fullName}}, {{otp}}, {{validMinutes}}"),
    };

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // IgnoreQueryFilters so tombstoned rows still count as "present" — avoids
        // re-inserting under a fresh Id and colliding on UX_EmailTemplate_templateKey.
        var existingKeys = await ctx.EmailTemplates
            .IgnoreQueryFilters()
            .Select(t => t.TemplateKey)
            .ToListAsync(ct);

        var toInsert = Specs
            .Where(s => !existingKeys.Contains(s.TemplateKey))
            .Select(s => new EmailTemplate
            {
                Id = DeterministicId.From("EmailTemplate", s.TemplateKey),
                TemplateKey = s.TemplateKey,
                Subject = s.Subject,
                HtmlBody = s.HtmlBody,
                IsActive = true,
                Notes = s.Notes,
                CreatedBy = "seed",
                CreatedOn = now
            })
            .ToList();

        if (toInsert.Count > 0)
        {
            ctx.EmailTemplates.AddRange(toInsert);
            await ctx.SaveChangesAsync(ct);
        }
    }
}
