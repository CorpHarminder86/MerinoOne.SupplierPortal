using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record DeactivateUserCommand(Guid Id) : IRequest<Unit>;

public class DeactivateUserCommandHandler : IRequestHandler<DeactivateUserCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IUserStatusService _status;
    private readonly IEffectivePermissionService _perms;

    public DeactivateUserCommandHandler(
        IAppDbContext db, ICurrentUser user, IUserStatusService status, IEffectivePermissionService perms)
    {
        _db = db;
        _user = user;
        _status = status;
        _perms = perms;
    }

    public async Task<Unit> Handle(DeactivateUserCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.Id, ct)
            ?? throw new NotFoundException("User", request.Id);

        if (!user.IsActive) return Unit.Value;

        user.IsActive = false;
        user.UpdatedOn = DateTime.UtcNow;
        user.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        await _db.SaveChangesAsync(ct);

        // Immediate lockout: evict the cached active-status (and permission set) so the deactivated
        // user is rejected on their very next request rather than lingering until token expiry.
        await _status.InvalidateAsync(user.Id, ct);
        await _perms.InvalidateAsync(new[] { user.Id }, ct);
        return Unit.Value;
    }
}
