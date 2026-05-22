using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PurchaseOrderLine : AuditableEntity
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int PositionNo { get; set; }
    public int SequenceNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public string OrderUnit { get; set; } = "EA";
    public decimal OrderQty { get; set; }
    public decimal PriceUnit { get; set; }
    public decimal Price { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal DiscountAmount { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? TaxCode { get; set; }
    public string? TaxDescription { get; set; }
}
