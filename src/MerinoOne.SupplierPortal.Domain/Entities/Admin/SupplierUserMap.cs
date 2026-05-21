using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class SupplierUserMap : AuditableEntity
{
    public Guid SupplierId { get; set; }
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public Guid SecRightId { get; set; }
    public SecRight? SecRight { get; set; }
}
