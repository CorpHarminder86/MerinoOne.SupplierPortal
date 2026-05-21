using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Comm;

public class CommunicationMessage : BaseAggregateRoot
{
    public Guid? PurchaseOrderId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid SenderUserId { get; set; }
    public Guid? ReceiverUserId { get; set; }
    public string MessageBody { get; set; } = string.Empty;
    public string? AttachmentUrl { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public bool IsSystemMessage { get; set; }
}
