using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record ReactivateUserCommand(Guid Id) : IRequest<Unit>;

public class ReactivateUserCommandHandler : IRequestHandler<ReactivateUserCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IUserStatusService _status;

    public ReactivateUserCommandHandler(IAppDbContext db, ICurrentUser user, IUserStatusService status)
    {
        _db = db;
        _user = user;
        _status = status;
    }

    public async Task<Unit> Handle(ReactivateUserCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.Id, ct)
            ?? throw new NotFoundException("User", request.Id);

        if (user.IsActive) return Unit.Value;

        user.IsActive = true;
        user.UpdatedOn = DateTime.UtcNow;
        user.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        await _db.SaveChangesAsync(ct);

        // Evict the cached "inactive" status so the reactivated user can authenticate again immediately.
        await _status.InvalidateAsync(user.Id, ct);
        return Unit.Value;
    }
}
