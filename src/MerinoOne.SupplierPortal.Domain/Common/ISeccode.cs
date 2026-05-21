using MerinoOne.SupplierPortal.Domain.Entities.Admin;

namespace MerinoOne.SupplierPortal.Domain.Common;

public interface ISeccode
{
    Guid SeccodeId { get; set; }
    Seccode? Owner { get; set; }
}
