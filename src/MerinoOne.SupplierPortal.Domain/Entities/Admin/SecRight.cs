using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class SecRight : AuditableEntity
{
    public Guid SeccodeId { get; set; }
    public Seccode? Seccode { get; set; }
    public string UserCode { get; set; } = string.Empty;
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; }
}
