using System.Security.Cryptography;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

public record CreateSupplierInviteCommand(CreateSupplierInviteRequest Body) : IRequest<SupplierInviteDetailDto>;

public class CreateSupplierInviteCommandValidator : AbstractValidator<CreateSupplierInviteCommand>
{
    public CreateSupplierInviteCommandValidator()
    {
        RuleFor(x => x.Body.LegalName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public class CreateSupplierInviteCommandHandler : IRequestHandler<CreateSupplierInviteCommand, SupplierInviteDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISupplierInviteSettings _settings;
    public CreateSupplierInviteCommandHandler(IAppDbContext db, ICurrentUser user, ISupplierInviteSettings settings)
    {
        _db = db;
        _user = user;
        _settings = settings;
    }

    public async Task<SupplierInviteDetailDto> Handle(CreateSupplierInviteCommand request, CancellationToken ct)
    {
        var email = request.Body.Email.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        // Block if there's still a pending (non-consumed, non-expired) invite for this email
        var existingPending = await _db.SupplierInvites
            .AnyAsync(i => i.Email == email && i.ConsumedAt == null && i.ExpiresAt > now, ct);
        if (existingPending)
            throw new ConflictException($"A pending invite already exists for '{email}'.");

        var token = GenerateUrlSafeToken();
        // Pull invite-token lifetime from the SystemSettings module (SupplierInvite.ExpiryDays).
        var expiryDays = _settings.ExpiryDays;

        var invite = new SupplierInvite
        {
            Id = Guid.NewGuid(),
            LegalName = request.Body.LegalName.Trim(),
            Email = email,
            InvitedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            InvitedAt = now,
            Token = token,
            ExpiresAt = now.AddDays(expiryDays),
            CreatedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode,
            CreatedOn = now,
        };
        _db.SupplierInvites.Add(invite);
        await _db.SaveChangesAsync(ct);

        return new SupplierInviteDetailDto(
            invite.Id, invite.Seq, invite.LegalName, invite.Email, invite.InvitedBy,
            invite.InvitedAt, invite.ExpiresAt, invite.ConsumedAt, invite.SupplierId,
            invite.Token, "Pending");
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
