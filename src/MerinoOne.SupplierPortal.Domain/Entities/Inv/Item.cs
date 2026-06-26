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

    // R4 (2026-06-22) — Addendum A3: LN-fed control flags. ASN line capture reads these (serial / lot
    // capture). NOT NULL with a named default in the migration so existing rows are safe.
    public bool IsSerialized { get; set; }
    public bool IsLotControlled { get; set; }

    // R4 (2026-06-26) — TSD R4 Addendum §3.2, Component 4 (Over-Ship Tolerance). The always-present fallback
    // over-ship ceiling (%). NOT NULL DEFAULT 0 (strict) guarantees every item resolves to a defined value;
    // SupplierItem.OverShipTolerancePct (nullable) overrides this when present (two-tier resolution §7.1).
    public decimal OverShipTolerancePct { get; set; }
}
