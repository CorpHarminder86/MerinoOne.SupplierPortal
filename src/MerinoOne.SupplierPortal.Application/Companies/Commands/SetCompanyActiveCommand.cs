using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Companies.Commands;

/// <summary>
/// Tenant-Admin: activate / deactivate a company in the current tenant. Deactivated companies drop out
/// of the active-company selector and a Tenant Admin's accessible set, but their historical data is
/// preserved (no soft-delete).
/// </summary>
public record SetCompanyActiveCommand(Guid Id, bool IsActive) : IRequest<Unit>;

public class SetCompanyActiveCommandHandler : IRequestHandler<SetCompanyActiveCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public SetCompanyActiveCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(SetCompanyActiveCommand request, CancellationToken ct)
    {
        var entity = await _db.TenantEntities.FirstOrDefaultAsync(e => e.Id == request.Id, ct)
            ?? throw new NotFoundException("Company", request.Id);

        if (entity.IsActive == request.IsActive)
            return Unit.Value;

        entity.IsActive = request.IsActive;
        entity.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        entity.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
