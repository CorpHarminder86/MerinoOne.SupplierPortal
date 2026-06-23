namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R4 (2026-06-23) — Transactional DOCUMENT ingestion pushed by Infor LN: Purchase Orders (+ lines),
// Delivery Schedules and Goods Receipts (GRN rows). These CREATE/UPSERT the live documents the portal
// operates on (the existing /grn-status + /payments endpoints only UPDATE pre-existing documents). All are
// X-APIKey + per-endpoint Integration.Inbound.* scope and reuse the InboundUpsertExecutor's
// TransactionalInboundEntity path (literal-company resolution, anti-spoof, canonical-hash idempotency,
// InforSyncLog / IntegrationError, endpoint-session telemetry). Company semantics match PushGrnStatusRequest.

// ----------------------------------- Purchase Order -----------------------------------

/// <summary>
/// One inbound PO line pushed by Infor LN. <see cref="ItemCode"/> / <see cref="TaxCode"/> are resolved to the
/// item / tax master of the resolved source company (resolve-or-leave-null + keep the snapshot). The natural
/// key within a PO is <see cref="PositionNo"/>.
/// </summary>
public record PoLineRecord(
    int PositionNo,
    int SequenceNo,
    string ItemCode,
    string? ItemDescription = null,
    string OrderUnit = "EA",
    decimal OrderQty = 0,
    decimal PriceUnit = 0,
    decimal Price = 0,
    decimal DiscountPct = 0,
    decimal DiscountAmount = 0,
    DateTime? DeliveryDate = null,
    string? TaxCode = null,
    string? TaxDescription = null);

/// <summary>
/// One inbound Purchase Order pushed by Infor LN. <see cref="PoNumber"/> is the natural key within the
/// resolved company. <see cref="SupplierCode"/> resolves the owning supplier (and the PO's seccode). PoType /
/// PoStatus are enum NAMEs (default Material / Released). Currency / payment-term / delivery-term resolve by
/// code against the resolved company (FK + snapshot).
/// </summary>
public record PoRecord(
    string PoNumber,
    string SupplierCode,
    DateTime PoDate,
    IReadOnlyList<PoLineRecord> Lines,
    string? PoType = null,
    string? PoStatus = null,
    string? PaymentTerms = null,
    string? DeliveryTerms = null,
    string? PaymentTermCode = null,
    string? DeliveryTermCode = null,
    string? CurrencyCode = null,
    string? Notes = null,
    string? ErpSyncId = null);

/// <summary>Inbound PO push body. See <see cref="PushGrnStatusRequest"/> for company semantics.</summary>
public record PushPurchaseOrdersRequest(
    string CompanyCode,
    IReadOnlyList<PoRecord> Orders);

// ----------------------------------- Delivery Schedule -----------------------------------

/// <summary>
/// One inbound delivery-schedule line pushed by Infor LN, tied to a PO by <see cref="PoNumber"/>. The natural
/// key for idempotent re-push is (PurchaseOrderId, ProposedDate). ScheduleStatus is the enum NAME (default
/// Proposed).
/// </summary>
public record DeliveryScheduleRecord(
    string PoNumber,
    DateTime ProposedDate,
    string? TimeWindow = null,
    string? VehicleInfo = null,
    string? ScheduleStatus = null);

/// <summary>Inbound delivery-schedule push body. See <see cref="PushGrnStatusRequest"/> for company semantics.</summary>
public record PushDeliverySchedulesRequest(
    string CompanyCode,
    IReadOnlyList<DeliveryScheduleRecord> Schedules);

// ----------------------------------- Goods Receipt (create) -----------------------------------

/// <summary>
/// One inbound goods-receipt (GRN) row pushed by Infor LN. CREATES the GoodsReceipt against a PO line resolved
/// by (<see cref="PoNumber"/>, <see cref="PoPositionNo"/>); the existing /grn-status endpoint then advances
/// <c>grnStatus</c> (and triggers the invoice auto-post). <see cref="GrnNumber"/> is the natural key within the
/// resolved company. New GRNs land as <c>GrnNotApproved</c>.
/// </summary>
public record GoodsReceiptRecord(
    string GrnNumber,
    string PoNumber,
    int PoPositionNo,
    decimal ReceivedQty,
    decimal? ShortQty = null,
    decimal? RejectedQty = null,
    DateTime? GrnDate = null,
    string? AsnNumber = null,
    string? ErpSyncId = null);

/// <summary>Inbound goods-receipt push body. See <see cref="PushGrnStatusRequest"/> for company semantics.</summary>
public record PushGoodsReceiptsRequest(
    string CompanyCode,
    IReadOnlyList<GoodsReceiptRecord> Receipts);
