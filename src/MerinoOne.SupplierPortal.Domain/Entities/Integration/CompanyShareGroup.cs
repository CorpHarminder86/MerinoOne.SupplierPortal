using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// Endpoint-wise table-sharing group. For a given <see cref="SharedEndpoint"/> the group's member
/// companies all read/write a single shared dataset stored under the <see cref="SourceTenantEntityId"/>
/// (source company). Each (tenant, endpoint, source) is unique; the resolver normalizes a member
/// company to its source on write so the dataset is stored once.
/// </summary>
public class CompanyShareGroup : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Which master endpoint this sharing applies to. Persisted as the enum name (string).</summary>
    public SharedEndpoint Endpoint { get; set; }

    /// <summary>The company under which the shared dataset is physically stored.</summary>
    public Guid SourceTenantEntityId { get; set; }
    public Admin.TenantEntity? SourceTenantEntity { get; set; }

    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    public ICollection<CompanyShareGroupMember> Members { get; set; } = new List<CompanyShareGroupMember>();
}
