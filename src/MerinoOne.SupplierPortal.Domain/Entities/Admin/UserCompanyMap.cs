using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// Grants a user access to one company (<see cref="TenantEntity"/>). A user may have many.
/// The mapped company's tenant MUST equal the user's tenant (enforced in the handler validator).
/// One map per user may be flagged <see cref="IsDefault"/> — the fallback active company.
/// </summary>
public class UserCompanyMap : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    public Guid TenantEntityId { get; set; }
    public TenantEntity? TenantEntity { get; set; }

    public bool IsDefault { get; set; }
}
