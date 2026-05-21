namespace MerinoOne.SupplierPortal.Domain.Common;

public interface ITenantScoped
{
    Guid? TenantId { get; set; }
    Guid? TenantEntityId { get; set; }
}
