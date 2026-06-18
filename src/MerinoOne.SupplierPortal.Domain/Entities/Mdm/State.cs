using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// State / province master — tenant-scoped, fed by Infor LN. Natural key = (TenantId, Code).
/// Always belongs to a <see cref="Country"/> (required).
/// </summary>
public class State : AuditableEntity, ITenantOwned, IHasCode
{
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public Guid CountryId { get; set; }
    public Country? Country { get; set; }

    public string? IsoCode { get; set; }                    // ISO 3166-2 subdivision
    public bool IsActive { get; set; } = true;
}
