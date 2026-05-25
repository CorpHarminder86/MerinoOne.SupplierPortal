using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

/// <summary>
/// Verifies the 6-digit OTP that was emailed alongside the invite link. The OTP
/// is single-use; 5 failed attempts invalidate the row and the supplier must
/// request a fresh OTP via <see cref="ResendInviteOtpCommand"/>.
/// </summary>
public record VerifyInviteOtpCommand(string Token, string Code) : IRequest<VerifyInviteOtpResponse>;

public class VerifyInviteOtpCommandValidator : AbstractValidator<VerifyInviteOtpCommand>
{
    public VerifyInviteOtpCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().Length(4, 8); // tolerant on length; default is 6
    }
}

public class VerifyInviteOtpCommandHandler : IRequestHandler<VerifyInviteOtpCommand, VerifyInviteOtpResponse>
{
    private const int MaxAttempts = 5;

    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _hasher;

    public VerifyInviteOtpCommandHandler(IAppDbContext db, IPasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<VerifyInviteOtpResponse> Handle(VerifyInviteOtpCommand request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var invite = await _db.SupplierInvites.FirstOrDefaultAsync(x => x.Token == request.Token, ct)
                     ?? throw new NotFoundException("SupplierInvite", request.Token);

        if (invite.ConsumedAt.HasValue)
            return new VerifyInviteOtpResponse(false, "This invite has already been used.", 0);
        if (invite.ExpiresAt < now)
            return new VerifyInviteOtpResponse(false, "This invite has expired.", 0);

        var otp = await _db.InviteOtps
            .Where(o => o.SupplierInviteId == invite.Id && o.ConsumedAt == null)
            .OrderByDescending(o => o.IssuedAt)
            .FirstOrDefaultAsync(ct);

        if (otp == null)
            return new VerifyInviteOtpResponse(false, "OTP not found. Request a new code.", 0);

        if (otp.ExpiresAt < now)
            return new VerifyInviteOtpResponse(false, "OTP has expired. Request a new code.", 0);

        var expected = _hasher.DeterministicHash(request.Code ?? string.Empty);
        if (!string.Equals(expected, otp.CodeHash, StringComparison.Ordinal))
        {
            otp.Attempts++;
            otp.UpdatedBy = "invite-otp-verify";
            otp.UpdatedOn = now;
            if (otp.Attempts >= MaxAttempts)
            {
                // Invalidate so the user is forced to request a fresh code.
                otp.ConsumedAt = now;
            }
            await _db.SaveChangesAsync(ct);

            var remaining = Math.Max(0, MaxAttempts - otp.Attempts);
            return new VerifyInviteOtpResponse(false, "Invalid OTP", remaining);
        }

        otp.ConsumedAt = now;
        otp.UpdatedBy = "invite-otp-verify";
        otp.UpdatedOn = now;
        await _db.SaveChangesAsync(ct);

        return new VerifyInviteOtpResponse(true, null, 0);
    }
}
