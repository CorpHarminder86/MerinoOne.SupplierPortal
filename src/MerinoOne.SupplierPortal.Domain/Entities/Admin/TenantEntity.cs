using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// A physical Infor LN company (logistic company code, e.g. 2000–7000) under a <see cref="Tenant"/>.
/// The active TenantEntity (header X-Active-Company) is the basis for the always-on company filter.
/// Tenant-scoped config (not company-scoped) so Tenant Admins manage companies tenant-wide.
/// </summary>
public class TenantEntity : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>Infor LN logistic company code (e.g. "2000"). Unique within the tenant.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
