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
/// item / tax master of the resolved source company (resolve-or-leave-null + keep the snapshot).
/// <para><b>Storage natural key within a PO is <see cref="PositionNo"/> ALONE.</b> <see cref="SequenceNo"/> is
/// still accepted (and validated) but is NEVER persisted as received — the portal ALWAYS stores sequenceNo = 1.
/// Multiple inbound lines that share a positionNo (any sequenceNo) are FOLDED into the single stored line for
/// that position.</para>
/// <para><b><see cref="OrderQty"/> vs <see cref="AdditionalQty"/> are mutually exclusive per line:</b>
/// <c>OrderQty &gt; 0</c> with <c>AdditionalQty = 0</c> → REPLACE (stored qty = OrderQty);
/// <c>OrderQty = 0</c> with <c>AdditionalQty ≠ 0</c> → ADD the signed delta to the current stored qty
/// (AdditionalQty may be NEGATIVE to reduce); both <c>0</c> → no-op (leave the qty unchanged);
/// both non-zero → the line is REJECTED (400).</para>
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
    string? TaxDescription = null,
    /// <summary>R4 (2026-06-30) — signed additive qty delta (may be negative to reduce). Mutually exclusive with a
    /// non-zero <see cref="OrderQty"/>: set OrderQty to REPLACE the absolute qty, or AdditionalQty to ADD to the
    /// current qty — not both. Default 0.</summary>
    decimal AdditionalQty = 0);

/// <summary>
/// One inbound Purchase Order pushed by Infor LN. <see cref="PoNumber"/> is the natural key within the
/// resolved company. The owning supplier (and the PO's seccode) resolves from EITHER
/// <see cref="ErpSupplierCode"/> (matched against <c>Supplier.ErpCode</c>) OR <see cref="SupplierCode"/>
/// (matched against <c>Supplier.SupplierCode</c>). When BOTH are supplied, <see cref="ErpSupplierCode"/> wins
/// (it is the ERP's authoritative identity); when NEITHER is supplied the row is rejected — at least one is
/// required. PoType / PoStatus are enum NAMEs (default Material / Released). Currency / payment-term /
/// delivery-term resolve by code against the resolved company (FK + snapshot).
/// </summary>
public record PoRecord(
    string PoNumber,
    string? SupplierCode,
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
    string? ErpSyncId = null,
    /// <summary>ERP supplier code (matched against <c>Supplier.ErpCode</c>). Takes priority over
    /// <see cref="SupplierCode"/> when both are supplied. One of the two is required.</summary>
    string? ErpSupplierCode = null);

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
