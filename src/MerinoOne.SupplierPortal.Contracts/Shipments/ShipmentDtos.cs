namespace MerinoOne.SupplierPortal.Contracts.Shipments;

public record DeliveryScheduleDto(
    Guid Id,
    int Seq,
    Guid PurchaseOrderId,
    string PoNumber,
    DateTime ProposedDate,
    string? TimeWindow,
    string? VehicleInfo,
    string ScheduleStatus,
    string? ApprovedBy,
    string? RejectionReason,
    DateTime CreatedOn);

public record AsnListItemDto(
    Guid Id,
    int Seq,
    string AsnNumber,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    DateTime ExpectedDeliveryDate,
    string? CarrierName,
    string? TrackingNumber,
    string AsnStatus,
    DateTime CreatedOn);

public record AsnDetailDto(
    Guid Id,
    int Seq,
    string AsnNumber,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    DateTime ExpectedDeliveryDate,
    string? TimeWindow,
    string? CarrierName,
    string? TrackingNumber,
    string? VehicleNumber,
    string? DriverName,
    string? DriverPhone,
    string AsnStatus,
    string? Notes,
    List<AsnLineDto> Lines);

public record AsnLineDto(
    Guid Id,
    Guid PurchaseOrderLineId,
    int PoPositionNo,
    string ItemCode,
    string? ItemDescription,
    string OrderUnit,
    decimal OrderQty,
    decimal ShippedQty,
    string? BatchNumber,
    DateTime? ExpiryDate);

public record ProposeDeliveryScheduleRequest(
    Guid PurchaseOrderId,
    DateTime ProposedDate,
    string? TimeWindow,
    string? VehicleInfo);

public record ApproveDeliveryScheduleRequest(bool Approve, string? RejectionReason);

public record CreateAsnRequest(
    Guid PurchaseOrderId,
    DateTime ExpectedDeliveryDate,
    string? TimeWindow,
    string? CarrierName,
    string? TrackingNumber,
    string? VehicleNumber,
    string? DriverName,
    string? DriverPhone,
    string? Notes,
    List<CreateAsnLineRequest> Lines);

public record CreateAsnLineRequest(
    Guid PurchaseOrderLineId,
    decimal ShippedQty,
    string? BatchNumber,
    DateTime? ExpiryDate);

public record UpdateAsnRequest(
    DateTime ExpectedDeliveryDate,
    string? TimeWindow,
    string? CarrierName,
    string? TrackingNumber,
    string? VehicleNumber,
    string? DriverName,
    string? DriverPhone,
    string? Notes,
    List<CreateAsnLineRequest> Lines);

public record GoodsReceiptDto(
    Guid Id,
    int Seq,
    string GrnNumber,
    Guid PurchaseOrderLineId,
    int PoPositionNo,
    string PoNumber,
    string ItemCode,
    Guid? AsnId,
    string? AsnNumber,
    decimal ReceivedQty,
    decimal ShortQty,
    decimal RejectedQty,
    DateTime GrnDate,
    string? ErpSyncId);

public record GoodsReceiptListItemDto(
    Guid Id,
    string GrnNumber,
    Guid PurchaseOrderLineId,
    int PoPositionNo,
    string PoNumber,
    string ItemCode,
    Guid? AsnId,
    string? AsnNumber,
    decimal ReceivedQty,
    decimal ShortQty,
    decimal RejectedQty,
    DateTime GrnDate,
    string? ErpSyncId);

// Integration admin DTOs (consumed by /integrations/sync-log + /integrations/errors)
public record InforSyncLogDto(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string Direction,
    string Status,
    string? IdempotencyKey,
    DateTime SyncedAt,
    string? Message,
    int RetryCount);

public record IntegrationErrorDto(
    Guid Id,
    string EntityType,
    Guid? EntityId,
    string ErrorMessage,
    string? StackTrace,
    DateTime OccurredAt,
    int RetryCount,
    bool Resolved);
