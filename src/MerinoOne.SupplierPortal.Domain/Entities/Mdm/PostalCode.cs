using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// Postal / PIN code master — tenant-scoped, fed by Infor LN. Natural key = (TenantId, Code). Maps to
/// <see cref="Country"/> (required) and optionally <see cref="StateId"/> / <see cref="CityId"/> so the
/// model fits country-only, country+state, and country+state+city addressing internationally.
/// </summary>
public class PostalCode : AuditableEntity, ITenantOwned, IHasCode
{
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string? Area { get; set; }

    public Guid CountryId { get; set; }
    public Country? Country { get; set; }

    public Guid? StateId { get; set; }
    public State? State { get; set; }

    public Guid? CityId { get; set; }
    public City? City { get; set; }

    public bool IsActive { get; set; } = true;
}
