using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class Seccode : AuditableEntity
{
    public SeccodeType SeccodeType { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? TenantEntityId { get; set; }

    public ICollection<SecRight> SecRights { get; set; } = new List<SecRight>();
}
