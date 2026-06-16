using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PaymentTerm : AuditableEntity, ICompanyScoped
{
    /// <summary>Tenant owner — covered by the always-on tenant filter.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The *source* company the (possibly shared) row is stored under. The company filter for this
    /// type is sharing-aware: TenantEntityId == ResolveSource(PaymentTerm, ActiveCompanyId).
    /// </summary>
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int NetDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
}
