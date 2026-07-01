using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Permissions.Queries;

/// <summary>
/// The catalog of human-assignable permissions, projected straight from the seeded
/// <c>admin.Permission</c> table so the UI can never drift from the backend. Service-to-service
/// (<c>Integration.Inbound.*</c>) and platform-tier (<c>Platform.*</c>) scopes are excluded — they
/// are never granted to a tenant business role.
/// </summary>
public record GetPermissionListQuery() : IRequest<List<PermissionListItemDto>>;

public class GetPermissionListQueryHandler : IRequestHandler<GetPermissionListQuery, List<PermissionListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetPermissionListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<PermissionListItemDto>> Handle(GetPermissionListQuery request, CancellationToken ct)
    {
        return await _db.Permissions
            .Where(p => !p.IsDeleted
                        && !p.Code.StartsWith("Integration.Inbound.")
                        && !p.Code.StartsWith("Platform."))
            .OrderBy(p => p.Module).ThenBy(p => p.Code)
            .Select(p => new PermissionListItemDto(
                p.Code,
                p.Name,
                p.Module ?? "General",
                p.Description))
            .ToListAsync(ct);
    }
}
