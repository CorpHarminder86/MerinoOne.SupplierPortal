using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Inv;

public class Item : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Uom { get; set; } = "EA";
    public string? HsnCode { get; set; }
    public bool IsActive { get; set; } = true;
}
