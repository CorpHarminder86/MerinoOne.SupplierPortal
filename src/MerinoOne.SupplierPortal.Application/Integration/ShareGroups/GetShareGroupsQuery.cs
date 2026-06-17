using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.ShareGroups;

/// <summary>
/// Tenant-Admin: list every endpoint-wise share group in the caller's tenant (optionally filtered by
/// endpoint), each with its live members resolved to company code + name. Share-group tables are
/// <c>ITenantOwned</c> (tenant-filtered, NOT company-filtered); a Tenant Admin manages them tenant-wide,
/// so this uses <c>IgnoreQueryFilters()</c> + explicit <c>!IsDeleted</c> + a tenant restriction
/// (established cross-cutting admin read pattern — see <c>MapSupplierCommand</c>).
/// </summary>
public record GetShareGroupsQuery(string? Endpoint = null) : IRequest<List<ShareGroupDto>>;

public class GetShareGroupsQueryHandler : IRequestHandler<GetShareGroupsQuery, List<ShareGroupDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GetShareGroupsQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<List<ShareGroupDto>> Handle(GetShareGroupsQuery request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        // Optional endpoint filter — only applied when it parses to a known SharedEndpoint.
        SharedEndpoint? endpointFilter = null;
        if (!string.IsNullOrWhiteSpace(request.Endpoint))
        {
            if (Enum.TryParse<SharedEndpoint>(request.Endpoint, ignoreCase: true, out var parsed))
                endpointFilter = parsed;
            else
                return new List<ShareGroupDto>(); // unknown endpoint → no groups
        }

        var groupsQuery = _db.CompanyShareGroups.IgnoreQueryFilters()
            .Where(g => !g.IsDeleted)
            .Where(g => g.TenantId == tenantId);

        if (endpointFilter.HasValue)
            groupsQuery = groupsQuery.Where(g => g.Endpoint == endpointFilter.Value);

        // Project the group + its source company (join TenantEntity). Members are loaded separately and
        // stitched in below so we control the live-member filter independently of the EF nav.
        var groups = await groupsQuery
            .Select(g => new
            {
                g.Id,
                g.Endpoint,
                g.SourceTenantEntityId,
                SourceCode = _db.TenantEntities.IgnoreQueryFilters()
                    .Where(e => e.Id == g.SourceTenantEntityId).Select(e => e.Code).FirstOrDefault(),
                SourceName = _db.TenantEntities.IgnoreQueryFilters()
                    .Where(e => e.Id == g.SourceTenantEntityId).Select(e => e.Name).FirstOrDefault(),
                g.Name,
                g.IsEnabled
            })
            .ToListAsync(ct);

        if (groups.Count == 0)
            return new List<ShareGroupDto>();

        var groupIds = groups.Select(g => g.Id).ToList();

        // Live members for those groups, resolved to code + name via TenantEntity.
        var members = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .Where(m => !m.IsDeleted)
            .Where(m => m.TenantId == tenantId)
            .Where(m => groupIds.Contains(m.CompanyShareGroupId))
            .Select(m => new
            {
                m.Id,
                m.CompanyShareGroupId,
                m.MemberTenantEntityId,
                MemberCode = _db.TenantEntities.IgnoreQueryFilters()
                    .Where(e => e.Id == m.MemberTenantEntityId).Select(e => e.Code).FirstOrDefault(),
                MemberName = _db.TenantEntities.IgnoreQueryFilters()
                    .Where(e => e.Id == m.MemberTenantEntityId).Select(e => e.Name).FirstOrDefault()
            })
            .ToListAsync(ct);

        var membersByGroup = members
            .GroupBy(m => m.CompanyShareGroupId)
            .ToDictionary(
                grp => grp.Key,
                grp => grp
                    .OrderBy(m => m.MemberCode)
                    .Select(m => new ShareGroupMemberDto(
                        m.Id, m.MemberTenantEntityId, m.MemberCode ?? string.Empty, m.MemberName ?? string.Empty))
                    .ToList());

        return groups
            .OrderBy(g => g.Endpoint.ToString())
            .ThenBy(g => g.SourceCode)
            .Select(g => new ShareGroupDto(
                g.Id,
                g.Endpoint.ToString(),
                g.SourceTenantEntityId,
                g.SourceCode ?? string.Empty,
                g.SourceName ?? string.Empty,
                g.Name,
                g.IsEnabled,
                membersByGroup.TryGetValue(g.Id, out var ms) ? ms : new List<ShareGroupMemberDto>()))
            .ToList();
    }
}
