using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class DeliverySchedule : BaseAggregateRoot
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public DateTime ProposedDate { get; set; }
    public string? TimeWindow { get; set; }
    public string? VehicleInfo { get; set; }
    public ScheduleStatus ScheduleStatus { get; set; } = ScheduleStatus.Proposed;
    public string? ApprovedBy { get; set; }
    public string? RejectionReason { get; set; }
}
