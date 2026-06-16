namespace MerinoOne.SupplierPortal.Domain.Common;

/// <summary>
/// Marker for company-scoped master data that participates in endpoint-wise table sharing
/// (PaymentTerm / DeliveryTerm). Carries the tenant + the *source* company under which the
/// (possibly shared) row is physically stored. The company filter for these types is
/// sharing-aware: <c>TenantEntityId == ResolveSource(endpoint, ActiveCompanyId)</c>.
/// </summary>
public interface ICompanyScoped
{
    Guid? TenantId { get; set; }
    Guid? TenantEntityId { get; set; }
}
