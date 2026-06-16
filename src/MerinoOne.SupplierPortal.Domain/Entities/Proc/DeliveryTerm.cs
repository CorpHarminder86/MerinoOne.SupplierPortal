using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class DeliveryTerm : AuditableEntity, ICompanyScoped
{
    /// <summary>Tenant owner — covered by the always-on tenant filter.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The *source* company the (possibly shared) row is stored under. The company filter for this
    /// type is sharing-aware: TenantEntityId == ResolveSource(DeliveryTerm, ActiveCompanyId).
    /// </summary>
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
