namespace MerinoOne.SupplierPortal.Domain.Common;

public interface IHasRowVersion
{
    byte[] RowVersion { get; set; }
}
