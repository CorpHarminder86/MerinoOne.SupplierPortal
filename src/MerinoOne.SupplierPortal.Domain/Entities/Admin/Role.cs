using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class Role : AuditableEntity, ITenantOwned
{
    /// <summary>
    /// Tenant that owns this role. Role names are unique PER TENANT (not global). Stored nullable
    /// in Phase 1; built-in roles seeded per tenant. Null only for design-time/system rows.
    /// </summary>
    public Guid? TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
