using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Mdm;

/// <summary>
/// City master — tenant-scoped, fed by Infor LN. Natural key = (TenantId, Code). Belongs to a
/// <see cref="Country"/> (required); <see cref="StateId"/> is nullable for state-less countries.
/// </summary>
public class City : AuditableEntity, ITenantOwned, IHasCode
{
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public Guid CountryId { get; set; }
    public Country? Country { get; set; }

    public Guid? StateId { get; set; }
    public State? State { get; set; }

    public bool IsActive { get; set; } = true;
}
