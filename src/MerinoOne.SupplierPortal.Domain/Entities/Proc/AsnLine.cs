using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class AsnLine : AuditableEntity
{
    public Guid AsnId { get; set; }
    public Asn? Asn { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal ShippedQty { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
}
