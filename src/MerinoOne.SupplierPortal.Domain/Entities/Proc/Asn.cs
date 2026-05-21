using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class Asn : BaseAggregateRoot
{
    public string AsnNumber { get; set; } = string.Empty;
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime ExpectedDeliveryDate { get; set; }
    public string? TimeWindow { get; set; }
    public string? CarrierName { get; set; }
    public string? TrackingNumber { get; set; }
    public string? VehicleNumber { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhone { get; set; }
    public AsnStatus AsnStatus { get; set; } = AsnStatus.Submitted;
    public string? Notes { get; set; }

    public ICollection<AsnLine> Lines { get; set; } = new List<AsnLine>();
}
