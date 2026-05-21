namespace MerinoOne.SupplierPortal.Contracts.Communication;

public record MessageDto(
    Guid Id,
    int Seq,
    Guid ThreadId,
    Guid SenderUserId,
    string SenderName,
    Guid? ReceiverUserId,
    string MessageBody,
    string? AttachmentUrl,
    DateTime SentAt,
    bool IsRead,
    bool IsSystemMessage);

public record ThreadSummaryDto(
    Guid ThreadId,
    string Subject,
    int MessageCount,
    int UnreadCount,
    DateTime LastMessageAt,
    string LastSenderName,
    string LastSnippet);

public record SendMessageRequest(
    Guid? ThreadId,
    Guid? ReceiverUserId,
    Guid? PurchaseOrderId,
    string MessageBody,
    string? AttachmentUrl);
