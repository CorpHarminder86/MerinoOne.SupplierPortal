using System.Security.Cryptography;
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

/// <summary>
/// Admin-initiated invite resend. Reissues a fresh token + expiry, persists a new InviteOtp row,
/// and re-sends both the Invite email and the InviteOtp email. Hard-blocked when the invite has
/// been consumed or cancelled. Throttled to 1 resend per 60s per invite.
/// </summary>
public record ResendSupplierInviteCommand(Guid InviteId) : IRequest<CreateSupplierInviteResponse>;

public class ResendSupplierInviteCommandValidator : AbstractValidator<ResendSupplierInviteCommand>
{
    public ResendSupplierInviteCommandValidator()
    {
        RuleFor(x => x.InviteId).NotEmpty();
    }
}

public class ResendSupplierInviteCommandHandler : IRequestHandler<ResendSupplierInviteCommand, CreateSupplierInviteResponse>
{
    private const int InviteOtpValidMinutes = 10;
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISupplierInviteSettings _settings;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly IOtpCodeGenerator _otp;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<ResendSupplierInviteCommandHandler> _logger;

    public ResendSupplierInviteCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        ISupplierInviteSettings settings,
        IEmailService email,
        IConfiguration config,
        IOtpCodeGenerator otp,
        IPasswordHasher hasher,
        ILogger<ResendSupplierInviteCommandHandler> logger)
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

    public async Task<CreateSupplierInviteResponse> Handle(ResendSupplierInviteCommand request, CancellationToken ct)
    {
        var invite = await _db.SupplierInvites.FirstOrDefaultAsync(i => i.Id == request.InviteId, ct)
                     ?? throw new NotFoundException("SupplierInvite", request.InviteId);

        if (invite.ConsumedAt.HasValue)
            throw new ConflictException("This invite has already been consumed and cannot be resent.");
        if (invite.CancelledAt.HasValue)
            throw new ConflictException("This invite has been cancelled and cannot be resent.");

        var now = DateTime.UtcNow;

        // Throttle — 1 resend per 60s per invite.
        if (invite.LastResentAt.HasValue && (now - invite.LastResentAt.Value) < ResendCooldown)
        {
            var waitSec = (int)Math.Ceiling((ResendCooldown - (now - invite.LastResentAt.Value)).TotalSeconds);
            throw new ConflictException($"Please wait {waitSec}s before resending this invite again.");
        }

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

        // Fresh token + extended expiry so the new email lands with a valid link.
        var newToken = GenerateUrlSafeToken();
        var newExpiry = now.AddDays(_settings.ExpiryDays);
        invite.Token = newToken;
        invite.ExpiresAt = newExpiry;
        invite.LastResentAt = now;
        invite.ResendCount += 1;
        invite.UpdatedBy = actor;
        invite.UpdatedOn = now;

        // Issue a fresh InviteOtp row so the previously-emailed code is superseded.
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
            CreatedBy = actor,
            CreatedOn = now,
        };
        _db.InviteOtps.Add(otpRow);
        await _db.SaveChangesAsync(ct);

        // Fire-and-log both emails. Persistence already committed — log and continue on failure.
        var configuredBase = _config["Web:BaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(configuredBase) ? configuredBase.TrimEnd('/') : "http://localhost:5114";
        var registrationUrl = $"{baseUrl}/register/{invite.Token}";
        try
        {
            await _email.SendInviteEmailAsync(
                invite.Email, invite.LegalName, invite.MobileNo, registrationUrl, invite.ExpiresAt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Resent invite email failed for {Email} (invite {InviteId}). Resend row was persisted.",
                invite.Email, invite.Id);
        }

        try
        {
            await _email.SendInviteOtpAsync(invite.Email, invite.LegalName, code, InviteOtpValidMinutes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Resent invite OTP email failed for {Email} (invite {InviteId}, otp {OtpId}).",
                invite.Email, invite.Id, otpRow.Id);
        }

        var detail = new SupplierInviteDetailDto(
            invite.Id, invite.Seq, invite.LegalName, invite.Email, invite.InvitedBy,
            invite.InvitedAt, invite.ExpiresAt, invite.ConsumedAt, invite.SupplierId,
            invite.Token, "Pending",
            invite.CancelledAt, invite.LastResentAt, invite.ResendCount);

        return new CreateSupplierInviteResponse(detail, invite.Token, registrationUrl);
    }

    // Duplicated from CreateSupplierInviteCommand. Identical generator; extracted later if more callsites land.
    private static string GenerateUrlSafeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
