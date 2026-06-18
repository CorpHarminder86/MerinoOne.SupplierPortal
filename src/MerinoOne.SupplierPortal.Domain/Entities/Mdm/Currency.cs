using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// Currency master — tenant-scoped ERP reference data fed by Infor LN. Shared across every company in
/// the tenant (no company sharing). Natural key = (TenantId, Code). Covered by the always-on tenant filter.
/// </summary>
public class Currency : AuditableEntity, ITenantOwned, IHasCode
{
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;        // ISO 4217, e.g. "INR"
    public string Description { get; set; } = string.Empty;
    public string? IsoCode { get; set; }                    // ISO 4217 alpha-3
    public string? Symbol { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public bool IsActive { get; set; } = true;
}
