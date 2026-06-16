using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

public record CreateSupplierInviteCommand(CreateSupplierInviteRequest Body) : IRequest<SupplierInviteDetailDto>;

public class CreateSupplierInviteCommandValidator : AbstractValidator<CreateSupplierInviteCommand>
{
    // Loose E.164: optional leading '+' then 8–15 digits. Empty/null is allowed.
    private static readonly Regex MobileRegex = new("^\\+?\\d{8,15}$", RegexOptions.Compiled);

    public CreateSupplierInviteCommandValidator()
    {
        RuleFor(x => x.Body.LegalName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Body.TenantEntityId).NotEmpty()
            .WithMessage("A company (TenantEntityId) is required for the invite.");
        RuleFor(x => x.Body.MobileNo)
            .Must(m => string.IsNullOrWhiteSpace(m) || MobileRegex.IsMatch(m!))
            .WithMessage("Mobile number must be 8–15 digits with an optional leading '+'.");
    }
}

public class CreateSupplierInviteCommandHandler : IRequestHandler<CreateSupplierInviteCommand, SupplierInviteDetailDto>
{
    private const int InviteOtpValidMinutes = 10;

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISupplierInviteSettings _settings;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly IOtpCodeGenerator _otp;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<CreateSupplierInviteCommandHandler> _logger;

    public CreateSupplierInviteCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        ISupplierInviteSettings settings,
        IEmailService email,
        IConfiguration config,
        IOtpCodeGenerator otp,
        IPasswordHasher hasher,
        ILogger<CreateSupplierInviteCommandHandler> logger)
    {
        _db = db;
        _user = user;
        _settings = settings;
        _email = email;
        _config = config;
        _otp = otp;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<SupplierInviteDetailDto> Handle(CreateSupplierInviteCommand request, CancellationToken ct)
    {
        var email = request.Body.Email.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        // Company must exist in the inviting admin's tenant. IgnoreQueryFilters + explicit tenant restriction
        // so a cross-tenant / unknown company is rejected as a 400 (the supplier inherits this company).
        var tenantId = _user.TenantId;
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => !e.IsDeleted && e.Id == request.Body.TenantEntityId
                                      && (tenantId == null || e.TenantId == tenantId), ct);
        if (company is null)
            throw new Common.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["tenantEntityId"] = new[] { "The selected company does not exist in your tenant." }
            });

        // Block if there's still a pending (non-consumed, non-expired) invite for this email
        var existingPending = await _db.SupplierInvites
            .AnyAsync(i => i.Email == email && i.ConsumedAt == null && i.ExpiresAt > now, ct);
        if (existingPending)
            throw new ConflictException($"A pending invite already exists for '{email}'.");

        var token = GenerateUrlSafeToken();
        // Pull invite-token lifetime from the SystemSettings module (SupplierInvite.ExpiryDays).
        var expiryDays = _settings.ExpiryDays;

        var mobile = string.IsNullOrWhiteSpace(request.Body.MobileNo)
            ? null
            : request.Body.MobileNo!.Trim();

        var invite = new SupplierInvite
        {
            Id = Guid.NewGuid(),
            TenantId = company.TenantId,         // explicit — the invite (and resulting supplier) is tenant-scoped
            TenantEntityId = company.Id,         // company the registered supplier inherits
            LegalName = request.Body.LegalName.Trim(),
            Email = email,
            MobileNo = mobile,
            InvitedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            InvitedAt = now,
            Token = token,
            ExpiresAt = now.AddDays(expiryDays),
            CreatedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            CreatedOn = now,
        };
        _db.SupplierInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        // Fire-and-log invite email. Persistence already committed — never roll back on send failure.
        try
        {
            var registrationUrl = BuildRegistrationUrl(invite.Token);
            await _email.SendInviteEmailAsync(
                invite.Email,
                invite.LegalName,
                invite.MobileNo,
                registrationUrl,
                invite.ExpiresAt,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Invite email send failed for {Email} (invite {InviteId}). Invite was persisted successfully.",
                invite.Email, invite.Id);
        }

        // Issue + persist the invite OTP, then email it as a separate message so the
        // supplier has the 6-digit code in hand without needing to click the link first.
        var code = _otp.Generate();
        var otpRow = new InviteOtp
        {
            Id = Guid.NewGuid(),
            SupplierInviteId = invite.Id,
            CodeHash = _hasher.DeterministicHash(code),
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(InviteOtpValidMinutes),
            Attempts = 0,
            ConsumedAt = null,
            CreatedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            CreatedOn = now,
        };
        _db.InviteOtps.Add(otpRow);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _email.SendInviteOtpAsync(invite.Email, invite.LegalName, code, InviteOtpValidMinutes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Invite OTP email send failed for {Email} (invite {InviteId}, otp {OtpId}). OTP row was persisted; supplier can request a resend.",
                invite.Email, invite.Id, otpRow.Id);
        }

        return new SupplierInviteDetailDto(
            invite.Id, invite.Seq, invite.LegalName, invite.Email, invite.InvitedBy,
            invite.InvitedAt, invite.ExpiresAt, invite.ConsumedAt, invite.SupplierId,
            invite.Token, "Pending");
    }

    private string BuildRegistrationUrl(string token)
    {
        var configured = _config["Web:BaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : "http://localhost:5114";
        return $"{baseUrl}/register/{token}";
    }

    private static string GenerateUrlSafeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
