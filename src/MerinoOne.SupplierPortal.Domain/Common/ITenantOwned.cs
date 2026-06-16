namespace MerinoOne.SupplierPortal.Domain.Common;

/// <summary>
/// Marker for tenant-scoped config / integration entities that are NOT company-scoped
/// (AppUser, Role, TenantEntity, UserCompanyMap, CompanyShareGroup(+Member), ApiKey,
/// SupplierInvite, EmailTemplate, EmailOutbox, InforEndpointMap/SyncLog/IntegrationError, …).
/// The always-on tenant filter applies to every type carrying a <see cref="TenantId"/>:
/// <see cref="ITenantOwned"/>, <see cref="ITenantScoped"/> and <see cref="ICompanyScoped"/>.
/// Tenant-scoped admins manage config tenant-wide, so these are deliberately not company-filtered.
/// </summary>
public interface ITenantOwned
{
    Guid? TenantId { get; set; }
}
