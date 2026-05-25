using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

/// <summary>
/// Re-issues a fresh invite OTP. Throttled to one request per 60 seconds — when the
/// previous OTP was issued within that window the command returns the remaining
/// cooldown so the UI can show a countdown without bouncing through a 4xx.
/// </summary>
public record ResendInviteOtpCommand(string Token) : IRequest<ResendInviteOtpResponse>;

public class ResendInviteOtpCommandValidator : AbstractValidator<ResendInviteOtpCommand>
{
    public ResendInviteOtpCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}

public class ResendInviteOtpCommandHandler : IRequestHandler<ResendInviteOtpCommand, ResendInviteOtpResponse>
{
    private const int InviteOtpValidMinutes = 10;
    private const int ResendCooldownSeconds = 60;

    private readonly IAppDbContext _db;
    private readonly IEmailService _email;
    private readonly IOtpCodeGenerator _otp;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<ResendInviteOtpCommandHandler> _logger;

    public ResendInviteOtpCommandHandler(
        IAppDbContext db,
        IEmailService email,
        IOtpCodeGenerator otp,
        IPasswordHasher hasher,
        ILogger<ResendInviteOtpCommandHandler> logger)
    {
        _db = db;
        _email = email;
        _otp = otp;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<ResendInviteOtpResponse> Handle(ResendInviteOtpCommand request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var invite = await _db.SupplierInvites.FirstOrDefaultAsync(x => x.Token == request.Token, ct)
                     ?? throw new NotFoundException("SupplierInvite", request.Token);

        if (invite.ConsumedAt.HasValue)
            return new ResendInviteOtpResponse(false, "This invite has already been used.", 0);
        if (invite.ExpiresAt < now)
            return new ResendInviteOtpResponse(false, "This invite has expired.", 0);

        // Throttle: check the newest OTP's IssuedAt regardless of consumed/expired state so
        // automated retries can't bypass the cooldown by spamming until a row "rolls over".
        var latest = await _db.InviteOtps
            .Where(o => o.SupplierInviteId == invite.Id)
            .OrderByDescending(o => o.IssuedAt)
            .FirstOrDefaultAsync(ct);

        if (latest != null)
        {
            var elapsed = (now - latest.IssuedAt).TotalSeconds;
            if (elapsed < ResendCooldownSeconds)
            {
                var retryAfter = (int)Math.Ceiling(ResendCooldownSeconds - elapsed);
                return new ResendInviteOtpResponse(false,
                    $"Please wait {retryAfter} second(s) before requesting another code.",
                    retryAfter);
            }
        }

        var code = _otp.Generate();
        var row = new InviteOtp
        {
            Id = Guid.NewGuid(),
            SupplierInviteId = invite.Id,
            CodeHash = _hasher.DeterministicHash(code),
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(InviteOtpValidMinutes),
            Attempts = 0,
            ConsumedAt = null,
            CreatedBy = "invite-otp-resend",
            CreatedOn = now,
        };
        _db.InviteOtps.Add(row);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _email.SendInviteOtpAsync(invite.Email, invite.LegalName, code, InviteOtpValidMinutes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Resend invite OTP email failed for {Email} (invite {InviteId}, otp {OtpId}). Row persisted; UI may surface a retry.",
                invite.Email, invite.Id, row.Id);
        }

        return new ResendInviteOtpResponse(true, "A new code has been emailed.", 0);
    }
}
