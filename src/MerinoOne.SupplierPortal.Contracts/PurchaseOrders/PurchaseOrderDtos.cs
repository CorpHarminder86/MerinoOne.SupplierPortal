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
    string PoResponseMode = "Manual",
    // PO list display: total amount (sum of line net = Price − DiscountAmount) + currency snapshot + the
    // header payment/delivery term strings. Trailing optional positionals.
    decimal TotalAmount = 0,
    string? CurrencyCode = null,
    string? PaymentTerms = null,
    string? DeliveryTerms = null);

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
    string? CurrencyCode,
    string PoStatus,
    DateTime? AcknowledgmentAt,
    DateTime? AcceptedAt,
    string? RejectionReason,
    int Version,
    string? BuyerName,
    string? ErpSyncId,
    string? Notes,
    List<PurchaseOrderLineDto> Lines,
    // R4 (2026-06-22): the owning supplier's PO-confirmation mode (R4: "AutoAccept" | "AcknowledgeToShip" |
    // "AcceptToShip"), joined from Supplier.PoConfirmationMode — replaces the UI's second GET per PO detail. Field
    // name kept as PoResponseMode for contract stability (the UI rename is Phase 5). Default "AcceptToShip".
    string PoResponseMode = "AcceptToShip",
    // PO header total = sum of each line's NetAmount (Price − DiscountAmount). Display-only (derived, not stored).
    decimal TotalAmount = 0,
    // R4 (2026-06-26) — Phase 5b / D1, D2: the owning supplier's action toggles, joined from
    // Supplier.AllowNegotiate / Supplier.AllowReject. They gate the PO-detail affordances: AllowReject=false hides
    // Reject/Decline; AllowNegotiate=false hides the Negotiation action. Trailing optional (default true) so older
    // positional callers stay valid and a missing value never hides an action it shouldn't.
    bool AllowNegotiate = true,
    bool AllowReject = true);

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
    string? TaxCode,
    string? TaxDescription = null,
    Guid? TaxId = null,
    // R4 (2026-06-23) — Serial/Lot capture support. ItemId links the PO line to the inv.Item master; the two
    // flags (left-joined from that Item) tell the ASN wizard which capture tab to show. serialized XOR
    // lot-controlled per item — at most one is true. Default false when the line has no resolvable Item.
    Guid? ItemId = null,
    bool IsSerialized = false,
    bool IsLotControlled = false,
    // Line net amount = Price (extended line amount) − DiscountAmount. The PO header TotalAmount is the sum of these.
    decimal NetAmount = 0,
    // R4 (2026-06-26) — Addendum §7.3 / DI-04 (ASN quantity tracking). The PO-line-picker the ASN wizard reads:
    // ShippedQtyToDate is the maintained cumulative; Balance is the nominal remaining (MAX(0, orderQty −
    // shippedQtyToDate)); OverShipAllowance is the tolerance-adjusted ceiling headroom (MAX(0, orderQty×(1+tol/100)
    // − shippedQtyToDate)). Derived at query time, never persisted. Surfaced SEPARATELY so a 0 balance with an
    // accepted over-ship allowance does not read inconsistent.
    decimal ShippedQtyToDate = 0,
    decimal Balance = 0,
    decimal OverShipAllowance = 0,
    // R4 (2026-06-26) — Addendum §5.3 / UC-ASN-10 (Phase 3, downward revision below shipped). True when an ERP
    // revision dropped orderQty BELOW the quantity already shipped (ShippedQtyToDate > OrderQty) — the line is
    // "over-shipped / qty reduced below shipped". Balance is already MAX(0,…) so it reads 0; this flag tells the
    // buyer/supplier the order shrank under the shipped cumulative. The atomic ASN guard auto-blocks further ASNs
    // on such a line (it reads orderQty live, revision-safe), so this is a display/exception signal, not a gate.
    bool IsOverShippedQtyReduced = false);

public record AcknowledgePoRequest(string? Notes = null);
// R4 (2026-06-26) — D2: accept is accept-only (the ProposedDate field is removed — counter-proposals go through
// PO negotiation). ProposePoDateRequest + ApproveProposalRequest retired with their endpoints.
public record AcceptPoRequest(string? Notes = null);
public record RejectPoRequest(string Reason);

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
