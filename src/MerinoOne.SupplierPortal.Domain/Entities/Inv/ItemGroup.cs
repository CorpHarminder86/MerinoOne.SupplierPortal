using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Inv;

/// <summary>
/// Item group master — company-scoped (shares the Item scope), fed by Infor LN. Natural key =
/// (TenantEntityId, Code). Covered by the sharing-aware company filter.
/// </summary>
public class ItemGroup : AuditableEntity, ICompanyScoped
{
    public Guid? TenantId { get; set; }
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
