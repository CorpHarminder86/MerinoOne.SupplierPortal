using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;

namespace MerinoOne.SupplierPortal.Domain.Entities.Inv;

/// <summary>
/// Item master — company-scoped (promoted from global so its FKs to the company-scoped
/// <see cref="ItemGroup"/> / <see cref="Unit"/> are unambiguous), fed by Infor LN. Natural key =
/// (TenantEntityId, Code). Sharing-aware company filter (SharedEndpoint.Item).
/// </summary>
public class Item : AuditableEntity, ICompanyScoped
{
    public Guid? TenantId { get; set; }
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? HsnCode { get; set; }

    public Guid? ItemGroupId { get; set; }
    public ItemGroup? ItemGroup { get; set; }

    public Guid? UnitId { get; set; }
    public Unit? Unit { get; set; }

    public bool IsActive { get; set; } = true;
}
