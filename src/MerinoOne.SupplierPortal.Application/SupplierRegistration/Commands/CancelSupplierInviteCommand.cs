using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

/// <summary>
/// Admin-initiated cancellation of a pending supplier invite. Hard-blocked when the invite
/// has already been consumed (supplier registered) or previously cancelled. Cancellation
/// takes precedence over natural expiry — an expired-but-not-cancelled invite can still be
/// cancelled so the audit trail shows the explicit action.
/// </summary>
public record CancelSupplierInviteCommand(Guid InviteId) : IRequest<SupplierInviteDetailDto>;

public class CancelSupplierInviteCommandValidator : AbstractValidator<CancelSupplierInviteCommand>
{
    public CancelSupplierInviteCommandValidator()
    {
        RuleFor(x => x.InviteId).NotEmpty();
    }
}

public class CancelSupplierInviteCommandHandler : IRequestHandler<CancelSupplierInviteCommand, SupplierInviteDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CancelSupplierInviteCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<SupplierInviteDetailDto> Handle(CancelSupplierInviteCommand request, CancellationToken ct)
    {
        var invite = await _db.SupplierInvites.FirstOrDefaultAsync(i => i.Id == request.InviteId, ct)
                     ?? throw new NotFoundException("SupplierInvite", request.InviteId);

        if (invite.ConsumedAt.HasValue)
            throw new ConflictException("This invite has already been consumed and cannot be cancelled.");
        if (invite.CancelledAt.HasValue)
            throw new ConflictException("This invite has already been cancelled.");

        var now = DateTime.UtcNow;
        invite.CancelledAt = now;
        invite.CancelledBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        invite.UpdatedBy = invite.CancelledBy;
        invite.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);

        return new SupplierInviteDetailDto(
            invite.Id, invite.Seq, invite.LegalName, invite.Email, invite.InvitedBy,
            invite.InvitedAt, invite.ExpiresAt, invite.ConsumedAt, invite.SupplierId,
            invite.Token, "Cancelled",
            invite.CancelledAt, invite.LastResentAt, invite.ResendCount);
    }
}
