namespace MerinoOne.SupplierPortal.Contracts.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §4.4 / §8.3) — grid read-model for proc.DeliverySchedule. One row per PO line, joined to
/// the PO line so the grid renders PO number, position, item, order qty + warehouse without a second round-trip.
/// Supersedes the pre-R5 shape (ProposedDate / TimeWindow / VehicleInfo / ScheduleStatus / ApprovedBy /
/// RejectionReason removed).
///
/// <para><b>RemainingToShip is DERIVED</b> from the R4 line balance — <c>MAX(0, orderQty − shippedQtyToDate)</c> —
/// computed in the projection; the schedule carries no shipped ledger of its own (§4.4). <c>ScheduledQty</c> is the
/// schedule's own committed qty (= line.orderQty at creation, refreshed on material Modify).</para>
/// </summary>
public record DeliveryScheduleDto(
    Guid Id,
    int Seq,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid PurchaseOrderLineId,
    int PositionNo,
    string ItemCode,
    string? ItemDescription,
    string OrderUnit,
    decimal OrderQty,
    decimal ShippedQtyToDate,
    decimal RemainingToShip,
    Guid SupplierId,
    string? SupplierName,
    decimal ScheduledQty,
    DateTime DeliveryDate,
    string Status,
    DateTime CreatedOn,
    // R4 §6.2 — whether the schedule's PO is shippable for its supplier's confirmation mode. The grid hides the
    // ASN checkbox when false (e.g. the PO was reset to Released by an ERP Modify). Computed server-side.
    bool IsShippable = true,
    // R11/R13 (D4) — the receiving warehouse. Warehouse is the PO LINE's raw code; WarehouseAddress* is the address
    // it resolves to (was ship-to). The one-warehouse-per-ASN grouping key for schedule-built ASNs.
    string? Warehouse = null,
    Guid? WarehouseAddressId = null,
    string? WarehouseAddressName = null);

/// <summary>
/// R5 (TSD R5 Addendum §7 / §8.3) — delivery-schedule grid filter/sort request. All filters are optional;
/// the grid sorts PO → Line → DeliveryDate ASC. <c>DeliveryDateFrom/To</c> are an inclusive day range.
/// </summary>
public record DeliveryScheduleFilterRequest(
    int Page = 1,
    int PageSize = 50,
    Guid? SupplierId = null,
    Guid? WarehouseAddressId = null,
    Guid? PurchaseOrderId = null,
    DateTime? DeliveryDateFrom = null,
    DateTime? DeliveryDateTo = null,
    string? Status = null);

/// <summary>
/// R5/R13 (TSD R5 Addendum §7) — the paged grid plus the "auto-hide warehouse" signal. <c>DistinctWarehouseCount</c>
/// is the number of distinct warehouse addresses across the schedule rows the supplier can see (BEFORE the warehouse
/// filter is applied); the UI hides the Warehouse filter when it is ≤ 1 (only one warehouse to choose from).
/// </summary>
public record DeliveryScheduleGridDto(
    PurchaseOrders.PagedResult<DeliveryScheduleDto> Page,
    int DistinctWarehouseCount,
    bool ShowWarehouseFilter);

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
    DateTime CreatedOn,
    // R11 (D4) — the receiving warehouse, so the list reads as "which dock is this going to" at a glance.
    string? Warehouse = null);

// R5 (review gap C2) — one row in the buyer-facing ASN approval queue (GET /api/asns/pending-approvals). Lists
// PendingApproval ASNs routed to the calling buyer (via any covered PO's BuyerUserId), or ALL PendingApproval
// ASNs in the tenant for an admin. SubmittedBy/On come from the ASN's latest Pending AsnApproval session.
public record AsnApprovalListItemDto(
    Guid AsnId,
    int Seq,
    string AsnNumber,
    string SupplierName,
    string? WarehouseAddressName,
    int PoCount,
    string? SubmittedBy,
    DateTime? SubmittedOn,
    // R11 (D4) — shown beside ship-to; the buyer approving a queue of ASNs cares which warehouse each is bound for.
    string? Warehouse = null);

// R5 (TSD R5 Addendum §10.2 / §4.6) — the latest approval session for an ASN, surfaced on the detail DTO.
// Null when the ASN has never been sent for approval. Status is Pending | Approved | Rejected.
public record AsnApprovalDto(
    Guid Id,
    string Status,
    string SubmittedBy,
    DateTime SubmittedOn,
    string? DecisionBy,
    DateTime? DecisionOn,
    string? Reason);

// R4 (2026-06-22) — Module 3. Nullable PO header (multi-PO), covered-PO list, draft/submit lifecycle fields,
// the auto-created draft invoice link (set after submit), and the ASN attachments.
// R5 (TSD R5 Addendum §4.5 / §10) — ShipToAddressId/Name (the §9 grouping key) + the latest Approval session.
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
    IReadOnlyList<Suppliers.DocumentAttachmentDto> Attachments,
    // R13 — the warehouse grouping key (set for schedule-built ASNs; null for legacy PO-picker ASNs) + its label,
    // and the latest approval session (null until first Send-for-Approval).
    Guid? WarehouseAddressId = null,
    string? WarehouseAddressName = null,
    AsnApprovalDto? Approval = null,
    // R4 §6.2 — for an editable (Draft/Rejected) ASN: true when Save Changes / Send For Approval are hard-blocked
    // (a covered PO is not shippable, or another ASN for the same PO is pending buyer approval). ShipBlockReason
    // carries the human message. False/null for locked ASNs and when shipping is permitted.
    bool ShipBlocked = false,
    string? ShipBlockReason = null,
    // R6 (2026-07-02) — outcome of the grouped draft-invoice generation at ASN approval: "Generated" / "Blocked"
    // / null (never attempted). InvoiceGenerationNote carries the Blocked cause (names the tax code(s)).
    // DraftInvoiceIds lists ALL invoices generated for this ASN (one per (currency, payment-term) group);
    // the scalar DraftInvoiceId above stays = first for back-compat.
    string? InvoiceGenerationStatus = null,
    string? InvoiceGenerationNote = null,
    IReadOnlyList<Guid>? DraftInvoiceIds = null,
    // R11 — Warehouse is the D4 grouping key, DERIVED from the covered PO(s) and read-only on every screen.
    // InvoiceNo/BillOfLading/PackingList are the D6 supplier-entered shipment references.
    // CreatedOn is the audit-block creation instant, surfaced because it is the LN payload's CreateDate (D10)
    // and the form shows it read-only (D12). The payload's ShipmentDate is SubmittedAt above (D11) — null until
    // the ASN is POSTED (R14), which is exactly when the shipment goes to the ERP.
    string? Warehouse = null,
    string? InvoiceNo = null,
    string? BillOfLading = null,
    string? PackingList = null,
    DateTime? CreatedOn = null,
    // R14 (2026-08-05) — PostedAt is the SHIPPING DATE: the instant the supplier clicked Post (D9). Null until
    // posted. AsnConfirmationRequired mirrors the owning supplier's flag so the UI knows which lifecycle to
    // render (Send-for-approval first, or Post straight from Draft). CanPost is the server's own verdict on
    // whether Post is the next legal action — mirrors AsnLifecycle.AssertCanPost so the button cannot disagree
    // with the guard. The UI must still AND it with !ShipBlocked.
    DateTime? PostedAt = null,
    string? PostedBy = null,
    bool AsnConfirmationRequired = true,
    bool CanPost = false);

/// <summary>One covered PO on a (possibly multi-PO) ASN.</summary>
public record AsnPurchaseOrderDto(
    Guid PurchaseOrderId,
    string PoNumber,
    string? CurrencyCode);

// R4 (2026-06-23) — Serial/Lot capture. One lot captured against a lot-controlled ASN line (input shape).
public record AsnLineLotInput(string LotNo, decimal Qty, DateOnly? ExpiryDate = null);

// R4 (2026-06-23) — Serial/Lot capture. One lot read back from a lot-controlled ASN line (carries ErpCode).
public record AsnLineLotDto(string LotNo, decimal Qty, DateOnly? ExpiryDate, string? ErpCode);

// R4 (2026-06-30) — Serial capture with per-serial expiry (input shape; mirrors AsnLineLotInput). ExpiryDate is
// optional + lenient — same policy as lots. One serial = one physical unit = one optional expiry.
public record AsnLineSerialInput(string SerialNumber, DateOnly? ExpiryDate = null);

// R4 (2026-06-30) — Serial read back from a serialized ASN line (carries ExpiryDate + ErpCode; mirrors AsnLineLotDto).
public record AsnLineSerialDto(string SerialNumber, DateOnly? ExpiryDate, string? ErpCode);

// R4 (2026-06-22) — Addendum A4: PositionNo + SequenceNo (snapshot from the source PO line) shown while
// building the ASN. PoPositionNo retained for back-compat (= PositionNo). PoNumber added (multi-PO lines).
// R4 (2026-06-23) — Serial/Lot capture: SerialNumbers (serialized items) + Lots (lot-controlled items), at
// most one populated per line (serialized XOR lot-controlled). Trailing optional so existing callers stay valid.
// R4 (2026-06-26) — Addendum §7.3 / DI-04: the PO line's cumulative ShippedQtyToDate plus the two DERIVED
// figures the UI shows SEPARATELY — Balance (nominal: MAX(0, orderQty − shippedQtyToDate)) and OverShipAllowance
// (tolerance-adjusted ceiling headroom: MAX(0, orderQty×(1+tol/100) − shippedQtyToDate)). None are persisted;
// they are computed at build time so "balance 0 with an accepted over-ship allowance" never looks inconsistent.
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
    IReadOnlyList<AsnLineSerialDto>? Serials = null,
    IReadOnlyList<AsnLineLotDto>? Lots = null,
    decimal ShippedQtyToDate = 0,
    decimal Balance = 0,
    decimal OverShipAllowance = 0,
    // R15 — schedule back-link + its delivery date, so the wizard can pre-check the originating schedule rows in
    // its step-1 grid and show the Delivery column on Quantities. Null for legacy PO-picked lines.
    Guid? DeliveryScheduleId = null,
    DateTime? ScheduleDeliveryDate = null);

// R4 (2026-06-22) — Module 3: ASN attachments reuse the existing Contracts.Suppliers.DocumentAttachmentDto
// (structurally identical; ownerEntityType='Asn', DocumentType.AsnAttachment). No duplicate type introduced
// (avoids the cross-namespace ambiguity in the Blazor global-usings).

// Pre-R5 request types — kept for source-compatibility while handlers are rewritten for R5.
// Backend-developer will replace these with the R5 create/upsert request shapes (§8 — no manual
// propose/approve; schedules are created by the Application layer when a PO becomes shippable).
[Obsolete("Pre-R5 shape — replaced by R5 DeliverySchedule creation via PO-shippable trigger (§8).")]
public record ProposeDeliveryScheduleRequest(
    Guid PurchaseOrderId,
    DateTime ProposedDate,
    string? TimeWindow,
    string? VehicleInfo);

[Obsolete("Pre-R5 shape — no manual approve/reject in R5; status is always Approved at creation.")]
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
    Guid? StagingKey = null,
    // R4 (2026-06-26) — §6.5 / UC-PO-09: admin gate-override reason. When the PO confirmation gate would BLOCK ASN
    // creation, a caller holding PurchaseOrder.OverrideGate may supply a non-empty reason to proceed anyway (audited).
    // Empty reason or missing permission → the normal block. Trailing optional.
    string? OverrideReason = null,
    // R11 (D6/D7) — supplier-entered shipment references, forwarded verbatim to LN. Max 50 each. Optional HERE
    // (a Draft may be parked with them blank) but MANDATORY at Send-For-Approval, so nothing reaches the ERP
    // without them. Trailing optional → source-compatible.
    string? InvoiceNo = null,
    string? BillOfLading = null,
    string? PackingList = null);

// R4 (2026-06-23) — Serial/Lot capture: Serials (serialized item) + Lots (lot-controlled item) — at most one
// populated per line. The other is ignored by the handler based on the line's Item flag. Used by BOTH Create
// and Update (UpdateAsnRequest.Lines is List<CreateAsnLineRequest>). Trailing optional → source-compatible.
public record CreateAsnLineRequest(
    Guid PurchaseOrderLineId,
    decimal ShippedQty,
    string? BatchNumber,
    DateTime? ExpiryDate,
    List<AsnLineSerialInput>? Serials = null,
    List<AsnLineLotInput>? Lots = null,
    // R15 — optional back-link to the originating delivery schedule (the schedule-driven wizard sends it; older
    // callers may omit). When supplied it must reference a live schedule of the SAME PurchaseOrderLineId.
    Guid? DeliveryScheduleId = null);

public record UpdateAsnRequest(
    DateTime ExpectedDeliveryDate,
    string? TimeWindow,
    string? CarrierName,
    string? TrackingNumber,
    string? VehicleNumber,
    string? DriverName,
    string? DriverPhone,
    string? Notes,
    List<CreateAsnLineRequest> Lines,
    // R11 (D6/D7) — see CreateAsnRequest. FULL-OVERWRITE semantics: the handler assigns all three unconditionally,
    // so a caller that omits one wipes it. The Blazor form always sends its current values.
    string? InvoiceNo = null,
    string? BillOfLading = null,
    string? PackingList = null);

// R4 (2026-06-26) — §6.5 / UC-PO-09: optional submit-time admin gate-override reason. When the PO confirmation
// gate would BLOCK draft submission, a caller holding PurchaseOrder.OverrideGate may supply a non-empty reason to
// proceed (audited). Empty reason or missing permission → the normal block.
// R4 (2026-06-26) — Phase 4 / §8.3 / UC-ATT-03: AcknowledgeMissingAttachments confirms proceeding past any
// Warning-level attachment requirement. First submit (false) with a missing Warning attachment returns a 200
// carrying ConfirmationRequired=true + the warning list; the client re-submits with true to proceed (the skip is
// audited). Mandatory-missing always blocks (400) regardless of this flag.
public record SubmitAsnRequest(string? OverrideReason = null, bool AcknowledgeMissingAttachments = false);

// R5/R13 (TSD R5 Addendum §9) — create an ASN from selected Delivery Schedule lines. All selected schedules must
// share ONE warehouseAddressId (cross-warehouse is blocked, UC-AS-02) and ONE supplier; lines MAY span multiple POs
// (UC-AS-01). The header is grouped by (supplierId, warehouseAddressId); each schedule → one AsnLine referencing its
// purchaseOrderLineId + deliveryScheduleId, ship qty defaulted to the line's remaining balance (editable, §9.2).
// ShipQtyByScheduleId optionally overrides the defaulted balance per schedule (keyed by deliveryScheduleId).
public record CreateAsnFromScheduleRequest(
    IReadOnlyList<Guid> ScheduleIds,
    DateTime ExpectedDeliveryDate,
    string? TimeWindow = null,
    string? CarrierName = null,
    string? TrackingNumber = null,
    string? VehicleNumber = null,
    string? DriverName = null,
    string? DriverPhone = null,
    string? Notes = null,
    IReadOnlyDictionary<Guid, decimal>? ShipQtyByScheduleId = null,
    Guid? StagingKey = null);

// R5 (TSD R5 Addendum §10.2) — supplier sends a Draft ASN for buyer approval.
// R14 — AcknowledgeMissingAttachments is now VESTIGIAL on this request: the attachment check moved to Post, so
// this path never returns ConfirmationRequired. Kept on the wire so existing clients keep compiling/posting.
public record SendForApprovalRequest(bool AcknowledgeMissingAttachments = false);

// R5 (TSD R5 Addendum §10.2) — buyer rejects a PendingApproval ASN. Reason is MANDATORY.
public record RejectAsnRequest(string Reason);

// R5 (TSD R5 Addendum §10.2) — buyer confirms a PendingApproval ASN.
// R14 — this no longer runs the submit path, so OverrideReason is accepted but IGNORED; the PO-gate override now
// belongs to PostAsnRequest. Kept on the wire for compatibility.
public record ApproveAsnRequest(string? OverrideReason = null);

// R14 — the supplier posts a confirmed (or, in No-confirmation mode, a Draft) ASN to the ERP. This is where the
// shipment references and the attachment policy are enforced, and where the §6.5 PO-gate override now applies:
// a caller holding PurchaseOrder.OverrideGate may supply a non-empty OverrideReason to push past a blocked PO
// (audited). AcknowledgeMissingAttachments confirms proceeding past a Warning-level attachment requirement —
// first call (false) returns 200 with ConfirmationRequired=true + the warning list and commits nothing.
public record PostAsnRequest(bool AcknowledgeMissingAttachments = false, string? OverrideReason = null);

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
