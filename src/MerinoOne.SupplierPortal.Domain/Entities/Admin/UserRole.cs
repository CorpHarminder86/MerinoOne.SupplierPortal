using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class UserRole : AuditableEntity
{
    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}
