using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// A company (<see cref="Admin.TenantEntity"/>) an <see cref="ApiKey"/> is bound to. One key may bind
/// several companies (Infor LN needs a single key usable across multiple companies). The inbound write
/// path normalizes the incoming company to its source and requires that source to be in the key's bound
/// company set. Mirrors <see cref="CompanyShareGroupMember"/>. Unique on (apiKeyId, tenantEntityId).
/// </summary>
public class ApiKeyCompany : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    public Guid ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }

    public Guid TenantEntityId { get; set; }
    public Admin.TenantEntity? TenantEntity { get; set; }
}
