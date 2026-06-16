using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// A member company of a <see cref="CompanyShareGroup"/>. <see cref="Endpoint"/> is denormalized
/// from the parent group so a unique index on (endpoint, memberTenantEntityId) can enforce that a
/// company belongs to at most one group per endpoint — making ResolveSource a total function.
/// </summary>
public class CompanyShareGroupMember : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    public Guid CompanyShareGroupId { get; set; }
    public CompanyShareGroup? CompanyShareGroup { get; set; }

    public Guid MemberTenantEntityId { get; set; }
    public Admin.TenantEntity? MemberTenantEntity { get; set; }

    /// <summary>Denormalized from the parent group — keys the (endpoint, member) uniqueness guard.</summary>
    public SharedEndpoint Endpoint { get; set; }
}
