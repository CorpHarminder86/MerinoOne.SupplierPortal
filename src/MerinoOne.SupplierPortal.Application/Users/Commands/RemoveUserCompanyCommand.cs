using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

/// <summary>
/// Revoke a user's access to one company (soft-delete the UserCompanyMap). If the removed map was the
/// user's default active company, the default flag is moved to another remaining company so the user
/// isn't left without a fallback active company.
/// </summary>
public record RemoveUserCompanyCommand(Guid UserId, Guid TenantEntityId) : IRequest<Unit>;

public class RemoveUserCompanyCommandHandler : IRequestHandler<RemoveUserCompanyCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RemoveUserCompanyCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(RemoveUserCompanyCommand request, CancellationToken ct)
    {
        var map = await _db.UserCompanyMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.AppUserId == request.UserId
                                      && m.TenantEntityId == request.TenantEntityId
                                      && !m.IsDeleted, ct)
            ?? throw new NotFoundException("UserCompanyMap", $"{request.UserId}/{request.TenantEntityId}");

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;

        map.IsDeleted = true;
        map.DeletedBy = actor;
        map.DeletedOn = now;
        map.UpdatedBy = actor;
        map.UpdatedOn = now;

        // Re-home the default flag if we just removed it.
        if (map.IsDefault)
        {
            map.IsDefault = false;
            var next = await _db.UserCompanyMaps.IgnoreQueryFilters()
                .Where(m => m.AppUserId == request.UserId && !m.IsDeleted && m.TenantEntityId != request.TenantEntityId)
                .OrderBy(m => m.CreatedOn)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                next.IsDefault = true;
                next.UpdatedBy = actor;
                next.UpdatedOn = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
