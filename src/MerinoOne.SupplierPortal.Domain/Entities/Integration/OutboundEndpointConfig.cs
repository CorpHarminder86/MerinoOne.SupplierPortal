using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §3.3 / D3. Transport config for an outbound integration endpoint (the reusable
/// outbound layer; IDM is the first consumer). One row per (tenant, endpointKey) e.g.
/// <c>IDM.Item.Create</c> / <c>.Update</c> / <c>.Delete</c>. Admin-scoped integration infrastructure —
/// tenant-scoped (<see cref="ITenantOwned"/>), NOT seccode-protected.
///
/// <para>Deliberately carries NO auth or base URL (D3): OAuth2 credentials and <c>apiBaseUrl</c> are resolved
/// per tenant from the existing <see cref="InforConnectionSetting"/> via <c>IInforConnectionProvider</c> /
/// <c>IInforTokenProvider</c>. <see cref="RelativePath"/> is appended to that base at send time.</para>
/// </summary>
public class OutboundEndpointConfig : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Target system discriminator, e.g. <c>"IDM"</c>.</summary>
    public string TargetSystem { get; set; } = string.Empty;

    /// <summary>Logical endpoint key, e.g. <c>"IDM.Item.Create"</c> | <c>".Update"</c> | <c>".Delete"</c>. Unique per tenant.</summary>
    public string EndpointKey { get; set; } = string.Empty;

    /// <summary>HTTP verb (POST | PUT | DELETE) — CHECK-constrained.</summary>
    public string HttpMethod { get; set; } = "POST";

    /// <summary>Path appended to the tenant's <see cref="InforConnectionSetting.ApiBaseUrl"/>, e.g. <c>/IDM/api/items</c>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Constant headers only (e.g. Content-Type), JSON object. Dynamic headers come from the mapping expression.</summary>
    public string? StaticHeadersJson { get; set; }

    /// <summary>Fixed ack-parser id (IDM responses are XML, D-R8-21), e.g. <c>"IdmXml"</c>.</summary>
    public string? AckParserKey { get; set; }

    /// <summary>IDM ACL default, e.g. <c>"Public"</c>.</summary>
    public string? DefaultAcl { get; set; }

    /// <summary>Soft on/off switch (disabled by default until validated).</summary>
    public bool IsEnabled { get; set; }
}
