using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// Country master — tenant-scoped ERP reference data fed by Infor LN. Natural key = (TenantId, Code).
/// Optional home <see cref="CurrencyId"/> FK (resolved by code on inbound).
/// </summary>
public class Country : AuditableEntity, ITenantOwned, IHasCode
{
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? IsoCode2 { get; set; }                   // ISO 3166-1 alpha-2
    public string? IsoCode3 { get; set; }                   // ISO 3166-1 alpha-3
    public string? TelephoneCode { get; set; }

    public Guid? CurrencyId { get; set; }
    public Currency? Currency { get; set; }

    public bool IsActive { get; set; } = true;
}
