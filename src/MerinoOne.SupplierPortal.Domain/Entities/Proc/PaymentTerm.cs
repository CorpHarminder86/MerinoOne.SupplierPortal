using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PaymentTerm : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int NetDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
}
