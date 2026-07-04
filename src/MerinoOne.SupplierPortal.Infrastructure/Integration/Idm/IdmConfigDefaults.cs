using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// 2026-07-05 — resolves the <c>config.acl</c>/<c>config.entityName</c> values every snapshot injects for the
/// mapping expressions to read (both create.jsonata and mutate.jsonata reference them identically — these
/// describe the pushed document's IDM class/security, not the HTTP verb, so Create's and Update's dispatches
/// share ONE source: the tenant's <c>IDM.Item.Create</c> <see cref="Domain.Entities.Integration.OutboundEndpointConfig"/>
/// row, editable on Integration › Outbound Endpoints. Previously these were hardcoded C# literals with no UI path
/// to change them; the same literals are now just the fallback when the row is missing/blank.
/// </summary>
internal static class IdmConfigDefaults
{
    private const string FallbackAcl = "Public";
    private const string FallbackEntityName = "MDS_GenericDocument";

    public static async Task<(string Acl, string EntityName)> ResolveAsync(IAppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var ep = await db.OutboundEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EndpointKey == "IDM.Item.Create" && !e.IsDeleted)
            .Select(e => new { e.DefaultAcl, e.EntityName })
            .FirstOrDefaultAsync(ct);

        return (
            string.IsNullOrWhiteSpace(ep?.DefaultAcl) ? FallbackAcl : ep.DefaultAcl,
            string.IsNullOrWhiteSpace(ep?.EntityName) ? FallbackEntityName : ep.EntityName);
    }
}
