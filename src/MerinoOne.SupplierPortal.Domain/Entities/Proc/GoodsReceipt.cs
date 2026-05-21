using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class GoodsReceipt : BaseAggregateRoot
{
    public string GrnNumber { get; set; } = string.Empty;
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public Guid? AsnId { get; set; }
    public Asn? Asn { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal ShortQty { get; set; }
    public decimal RejectedQty { get; set; }
    public DateTime GrnDate { get; set; } = DateTime.UtcNow;
    public string? ErpSyncId { get; set; }
}
