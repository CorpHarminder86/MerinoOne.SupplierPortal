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

// R4 (2026-06-22) — Module 3. Reshaped for multi-PO ASNs (Q1): PurchaseOrderId/PoNumber are NULLABLE now
// (set for single-PO back-compat, null for multi-PO). PurchaseOrders carries the full covered-PO list, and
// PoSummary gives a display string ("PO-1, PO-2" or "PO-1") so the list grid renders without a join.
public record AsnListItemDto(
    Guid Id,
    int Seq,
    string AsnNumber,
    Guid? PurchaseOrderId,
    string? PoNumber,
    string PoSummary,
    int PoCount,
    Guid SupplierId,
    string SupplierName,
    DateTime ExpectedDeliveryDate,
    string? CarrierName,
    string? TrackingNumber,
    string AsnStatus,
    DateTime? SubmittedAt,
    DateTime CreatedOn);

// R4 (2026-06-22) — Module 3. Nullable PO header (multi-PO), covered-PO list, draft/submit lifecycle fields,
// the auto-created draft invoice link (set after submit), and the ASN attachments.
public record AsnDetailDto(
    Guid Id,
    int Seq,
    string AsnNumber,
    Guid? PurchaseOrderId,
    string? PoNumber,
    IReadOnlyList<AsnPurchaseOrderDto> PurchaseOrders,
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
    DateTime? SubmittedAt,
    string? SubmittedBy,
    string? ErpSyncId,
    string? ErpCode,
    Guid? DraftInvoiceId,
    bool IsLocked,
    List<AsnLineDto> Lines,
    IReadOnlyList<Suppliers.DocumentAttachmentDto> Attachments);

/// <summary>One covered PO on a (possibly multi-PO) ASN.</summary>
public record AsnPurchaseOrderDto(
    Guid PurchaseOrderId,
    string PoNumber,
    string? CurrencyCode);

// R4 (2026-06-23) — Serial/Lot capture. One lot captured against a lot-controlled ASN line (input shape).
public record AsnLineLotInput(string LotNo, decimal Qty, DateOnly? ExpiryDate = null);

// R4 (2026-06-23) — Serial/Lot capture. One lot read back from a lot-controlled ASN line (carries ErpCode).
public record AsnLineLotDto(string LotNo, decimal Qty, DateOnly? ExpiryDate, string? ErpCode);

// R4 (2026-06-22) — Addendum A4: PositionNo + SequenceNo (snapshot from the source PO line) shown while
// building the ASN. PoPositionNo retained for back-compat (= PositionNo). PoNumber added (multi-PO lines).
// R4 (2026-06-23) — Serial/Lot capture: SerialNumbers (serialized items) + Lots (lot-controlled items), at
// most one populated per line (serialized XOR lot-controlled). Trailing optional so existing callers stay valid.
public record AsnLineDto(
    Guid Id,
    Guid PurchaseOrderLineId,
    Guid PurchaseOrderId,
    string PoNumber,
    int PoPositionNo,
    int? PositionNo,
    int? SequenceNo,
    string ItemCode,
    string? ItemDescription,
    string OrderUnit,
    decimal OrderQty,
    decimal ShippedQty,
    string? BatchNumber,
    DateTime? ExpiryDate,
    IReadOnlyList<string>? SerialNumbers = null,
    IReadOnlyList<AsnLineLotDto>? Lots = null);

// R4 (2026-06-22) — Module 3: ASN attachments reuse the existing Contracts.Suppliers.DocumentAttachmentDto
// (structurally identical; ownerEntityType='Asn', DocumentType.AsnAttachment). No duplicate type introduced
// (avoids the cross-namespace ambiguity in the Blazor global-usings).

public record ProposeDeliveryScheduleRequest(
    Guid PurchaseOrderId,
    DateTime ProposedDate,
    string? TimeWindow,
    string? VehicleInfo);

public record ApproveDeliveryScheduleRequest(bool Approve, string? RejectionReason);

// R4 (2026-06-22) — Module 3 (Q1 multi-PO). PurchaseOrderIds is the authoritative covered-PO list. The legacy
// scalar PurchaseOrderId is OPTIONAL: when only it is supplied (single PO) the handler treats it as a one-PO
// ASN; when PurchaseOrderIds has >1 entry the header PO is left null and the junction is populated. Each line
// carries its own PurchaseOrderLineId; the handler resolves the owning PO from the line (must be in the set).
public record CreateAsnRequest(
    Guid? PurchaseOrderId,
    IReadOnlyList<Guid>? PurchaseOrderIds,
    DateTime ExpectedDeliveryDate,
    string? TimeWindow,
    string? CarrierName,
    string? TrackingNumber,
    string? VehicleNumber,
    string? DriverName,
    string? DriverPhone,
    string? Notes,
    List<CreateAsnLineRequest> Lines,
    // R4 (2026-06-23) — deferred attachments: files uploaded during creation go to ownerEntityType='Staging' under
    // this client-generated key; CreateAsn rebinds them onto the new ASN (AsnAttachmentRebinder). Trailing optional.
    Guid? StagingKey = null);

// R4 (2026-06-23) — Serial/Lot capture: Serials (serialized item) + Lots (lot-controlled item) — at most one
// populated per line. The other is ignored by the handler based on the line's Item flag. Used by BOTH Create
// and Update (UpdateAsnRequest.Lines is List<CreateAsnLineRequest>). Trailing optional → source-compatible.
public record CreateAsnLineRequest(
    Guid PurchaseOrderLineId,
    decimal ShippedQty,
    string? BatchNumber,
    DateTime? ExpiryDate,
    List<string>? Serials = null,
    List<AsnLineLotInput>? Lots = null);

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

// R4 (2026-06-22) — Migration 0021 exposed: GrnStatus (ERP-owned receipt status), GrnApprovedAt, IssueReported
// (ERP remark), and the deterministic GRN→Invoice link (InvoiceId + denormalised InvoiceNumber). Added as
// trailing defaulted params so existing positional callers stay source-compatible.
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
    string? ErpSyncId,
    string GrnStatus = "",
    DateTime? GrnApprovedAt = null,
    string? IssueReported = null,
    Guid? InvoiceId = null,
    string? InvoiceNumber = null);

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
    string? ErpSyncId,
    string GrnStatus = "",
    DateTime? GrnApprovedAt = null,
    string? IssueReported = null,
    Guid? InvoiceId = null,
    string? InvoiceNumber = null);

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
