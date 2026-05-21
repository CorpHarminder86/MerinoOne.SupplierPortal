namespace MerinoOne.SupplierPortal.Domain.Common;

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTime? DeletedOn { get; set; }
    string? DeletedBy { get; set; }
}
