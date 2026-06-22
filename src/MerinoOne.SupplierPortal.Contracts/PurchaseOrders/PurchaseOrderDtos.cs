namespace MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

public record PurchaseOrderListItemDto(
    Guid Id,
    int Seq,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    string SupplierCode,
    string PoType,
    DateTime PoDate,
    string PoStatus,
    int Version,
    DateTime CreatedOn,
    // R4 (2026-06-22): the owning supplier's PO-response behaviour ("Manual" | "Auto"), joined from
    // Supplier.PoResponseMode. Lets the PO list gate accept/reject affordances per-row without a second
    // GET /api/suppliers/{id} per PO. Trailing optional positional — defaults to "Manual".
    string PoResponseMode = "Manual");

public record PagedResult<T>(
    List<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public record PurchaseOrderDetailDto(
    Guid Id,
    int Seq,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    string SupplierCode,
    Guid? BuyerUserId,
    string PoType,
    DateTime PoDate,
    string? PaymentTerms,
    string? DeliveryTerms,
    string PoStatus,
    DateTime? AcknowledgmentAt,
    DateTime? AcceptedAt,
    string? RejectionReason,
    DateTime? ProposedDeliveryDate,
    int Version,
    string? BuyerName,
    string? ErpSyncId,
    string? Notes,
    List<PurchaseOrderLineDto> Lines,
    // R4 (2026-06-22): the owning supplier's PO-response behaviour ("Manual" | "Auto"), joined from
    // Supplier.PoResponseMode — replaces the UI's second GET /api/suppliers/{supplierId} per PO detail.
    // Trailing optional positional — defaults to "Manual".
    string PoResponseMode = "Manual");

public record PurchaseOrderLineDto(
    Guid Id,
    int PositionNo,
    int SequenceNo,
    string ItemCode,
    string? ItemDescription,
    string OrderUnit,
    decimal OrderQty,
    decimal PriceUnit,
    decimal Price,
    decimal DiscountPct,
    decimal DiscountAmount,
    DateTime? DeliveryDate,
    string? TaxCode);

public record AcknowledgePoRequest(string? Notes = null);
public record AcceptPoRequest(DateTime? ProposedDate, string? Notes = null);
public record RejectPoRequest(string Reason);
public record ProposePoDateRequest(DateTime ProposedDate);
public record ApproveProposalRequest(string? Comment = null);

public record DeliveryScheduleListItemDto(
    Guid Id,
    Guid PurchaseOrderId,
    string PoNumber,
    string SupplierName,
    DateTime ProposedDate,
    string? TimeWindow,
    string? VehicleInfo,
    string ScheduleStatus,
    string? ApprovedBy,
    DateTime CreatedOn);

public record ApproveScheduleRequest(string? Comment = null);
public record RejectScheduleRequest(string Reason);
