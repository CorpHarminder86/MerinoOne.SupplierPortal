using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// Unit of measure master — company-scoped (shares the Item scope so Item→Unit FKs are unambiguous), fed
/// by Infor LN. Natural key = (TenantEntityId, Code). <see cref="ConversionFactor"/> converts to the
/// <see cref="BaseUnitId"/> (null ⇒ this row is itself a base unit). Covered by the sharing-aware company filter.
/// </summary>
public class Unit : AuditableEntity, ICompanyScoped
{
    public Guid? TenantId { get; set; }
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public UnitType UnitType { get; set; } = UnitType.Quantity;
    public string? IsoCode { get; set; }                    // UN/ECE Rec 20, e.g. "KGM"
    public int DecimalPlaces { get; set; } = 2;

    /// <summary>Factor to the base unit of the same dimension (1 for a base unit).</summary>
    public decimal ConversionFactor { get; set; } = 1m;

    public Guid? BaseUnitId { get; set; }                   // null ⇒ this is the base unit
    public Unit? BaseUnit { get; set; }

    public bool IsActive { get; set; } = true;
}
