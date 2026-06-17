using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Communication;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Communication.Queries;

/// <summary>
/// Recipients the caller may start a new message thread with (compose picker).
/// Model: supplier ↔ internal staff. A supplier user sees internal staff (Buyer/Finance/Admin); an
/// internal user sees every other active user in the tenant (supplier users + colleagues). Tenant scoping
/// is enforced by the always-on AppUser tenant filter — the query never crosses tenants.
/// </summary>
public record GetMessageRecipientsQuery() : IRequest<List<MessageRecipientDto>>;

public class GetMessageRecipientsQueryHandler : IRequestHandler<GetMessageRecipientsQuery, List<MessageRecipientDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GetMessageRecipientsQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<List<MessageRecipientDto>> Handle(GetMessageRecipientsQuery request, CancellationToken ct)
    {
        if (!_user.IsAuthenticated) throw new ForbiddenException();

        var sender = await _db.AppUsers
            .FirstOrDefaultAsync(u => u.UserCode == _user.UserCode, ct)
            ?? throw new NotFoundException("AppUser", _user.UserCode);

        // AppUser is tenant-filtered, so this is already scoped to the caller's tenant.
        var q = _db.AppUsers.Where(u => u.IsActive && u.Id != sender.Id);

        // Supplier user → internal staff only. Internal user → everyone else in the tenant.
        if (!sender.IsInternal)
            q = q.Where(u => u.IsInternal);

        var rows = await q
            .OrderByDescending(u => u.IsInternal)
            .ThenBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.UserCode,
                u.IsInternal,
                Roles = u.UserRoles.Where(ur => !ur.IsDeleted).Select(ur => ur.Role!.Name).ToList()
            })
            .ToListAsync(ct);

        return rows
            .Select(u => new MessageRecipientDto(u.Id, u.FullName, u.UserCode, u.IsInternal, string.Join(", ", u.Roles)))
            .ToList();
    }
}
