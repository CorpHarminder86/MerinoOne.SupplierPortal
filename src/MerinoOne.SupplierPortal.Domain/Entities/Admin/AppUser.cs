using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class AppUser : AuditableEntity, ITenantOwned
{
    /// <summary>
    /// The single tenant this user belongs to. NOT NULL for tenant users; null ONLY for the
    /// cross-tenant Platform Admin. Stored nullable in Phase 1; tightened to NOT NULL post-backfill.
    /// Fixed user attribute — not selectable. Every UserCompanyMap must reference this tenant.
    /// </summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string UserCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public bool IsMfaEnabled { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<SupplierUserMap> SupplierMaps { get; set; } = new List<SupplierUserMap>();
    public ICollection<UserCompanyMap> CompanyMaps { get; set; } = new List<UserCompanyMap>();
}
