using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PurchaseOrder : BaseAggregateRoot
{
    public string PoNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Guid? BuyerUserId { get; set; }
    public PoType PoType { get; set; }
    public DateTime PoDate { get; set; }
    public string? PaymentTerms { get; set; }
    public string? DeliveryTerms { get; set; }
    public PoStatus PoStatus { get; set; } = PoStatus.Released;
    public DateTime? AcknowledgmentAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? ProposedDeliveryDate { get; set; }
    public int Version { get; set; } = 1;
    public string? ErpSyncId { get; set; }
    public string? Notes { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}
