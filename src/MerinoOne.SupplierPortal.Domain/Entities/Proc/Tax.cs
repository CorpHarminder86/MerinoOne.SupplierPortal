using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// Tax master — company-scoped (sharing-aware), ERP-fed (Q6 FINAL). Mirrors <see cref="DeliveryTerm"/>:
/// natural key = (TenantEntityId, Code); the company filter is sharing-aware via
/// <c>ResolveSource(SharedEndpoint.Tax, ActiveCompanyId)</c>. Referenced by <see cref="PurchaseOrderLine.TaxId"/>
/// (the line keeps its denormalized <c>taxCode</c>/<c>taxDescription</c> snapshot for display when the FK points at
/// a Tax row from an unshared source company).
/// </summary>
public class Tax : AuditableEntity, ICompanyScoped
{
    /// <summary>Tenant owner — covered by the always-on tenant filter.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The *source* company the (possibly shared) row is stored under. The company filter for this type is
    /// sharing-aware: TenantEntityId == ResolveSource(Tax, ActiveCompanyId).
    /// </summary>
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal? TaxRate { get; set; }
    public bool IsActive { get; set; } = true;
}
