namespace MerinoOne.SupplierPortal.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Seq { get; set; }
}
