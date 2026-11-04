namespace MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

// ============================================================================================
// R4 (2026-06-24) — PO Negotiation contracts. Mirrors the SupplierChangeRequest DTO layout.
//
// A supplier raises a negotiation on a Released/Acknowledged PO proposing revised qty / delivery
// dates per line (never mutating the live PO lines — ERP stays the line master); the buyer approves
// (→ PO Approved + ERP round-trip via the outbox) or rejects (→ PO reverts to its captured previous
// status). These DTOs back the supplier "PO Change Request" list, the buyer review queue, and the
// buyer diff (original → negotiated) detail view.
//
// All enum-valued fields serialize as the enum NAME (string) — the same convention the entity uses
// (no DB CHECK; the C# enum is the guard) — so the UI binds against stable string values.
//
// R18 (2026-11-03) — re-negotiation rounds. The buyer may counter a supplier's round, and the supplier may accept,
// decline or counter a buyer's round; the number of rounds is unlimited. Each counter is a NEW round row (its parent
// round → Countered), numbered by RoundNo and linked by ParentNegotiationId / HeadNegotiationId.
// ============================================================================================

// ---------------- Request bodies ----------------

/// <summary>
/// A single proposed line delta in a create request. Only lines whose qty, delivery date OR unit price
/// actually differ from the live PO line are persisted (the handler drops no-op lines).
/// </summary>
public record PoNegotiationLineInput(
    Guid PurchaseOrderLineId,
    decimal NegotiatedQty,
    DateTime? NegotiatedDeliveryDate,
    decimal NegotiatedPrice);

/// <summary>
/// Supplier raises a PO negotiation. At least one line must differ from the PO (else 400). The PO must be
/// Released or Acknowledged (else 409).
/// </summary>
public record CreatePoNegotiationRequest(
    Guid PurchaseOrderId,
    string? Notes,
    List<PoNegotiationLineInput> Lines);

/// <summary>Buyer rejects a submitted negotiation (→ Rejected; PO reverts to its previous status). Reason required.</summary>
public record RejectPoNegotiationRequest(string Reason);

/// <summary>
/// R18 (2026-11-03) — a counter-party's counter to a negotiation round: its desired values per proposed line, in the
/// same <see cref="PoNegotiationLineInput"/> shape as a create. Used by the buyer (<c>/counter</c>, against a
/// supplier round) and by the supplier (<c>/supplier-counter</c>, against a buyer round).
/// </summary>
public record CounterPoNegotiationRequest(
    string? Notes,
    List<PoNegotiationLineInput> Lines);

/// <summary>R18 (2026-11-03) — supplier declines a buyer's counter. Reason required.</summary>
public record DeclinePoNegotiationRequest(string Reason);

// ---------------- Read models ----------------

/// <summary>One row in the negotiation list grid (supplier's own / buyer review queue).</summary>
public record PoNegotiationListItemDto(
    Guid Id,
    string PoNumber,
    string SupplierName,
    string NegotiationStatus,
    int LineCount,
    DateTime SubmittedAt,
    // R18 (2026-11-03) — which round of its chain this row is (1 = the supplier's original proposal) and which side
    // submitted it ("Supplier" | "Buyer"). Trailing optionals: existing positional producers stay valid.
    int RoundNo = 1,
    string ProposedBy = "Supplier");

/// <summary>
/// A single negotiation line for the buyer diff view: the original snapshot (qty + delivery date at submit)
/// next to the negotiated values, plus the source PO-line natural key (position/sequence/item).
/// </summary>
public record PoNegotiationLineDto(
    Guid PurchaseOrderLineId,
    int PositionNo,
    int SequenceNo,
    string ItemCode,
    decimal OriginalQty,
    decimal NegotiatedQty,
    DateTime? OriginalDeliveryDate,
    DateTime? NegotiatedDeliveryDate,
    decimal OriginalPrice,
    decimal NegotiatedPrice,
    // Display context for the diff grid — the PO line's item description and order unit — plus that unit's decimal
    // places (resolved server-side via IUnitDecimalsResolver; 3 when unknown/blank) so quantities render at the right
    // precision. Trailing optionals: existing positional producers stay valid.
    string? ItemDescription = null,
    string? OrderUnit = null,
    int UnitDecimalPlaces = 3,
    // Whether this line carries a proposed change versus the live PO line. Persisted negotiation lines always do
    // (no-op lines are dropped at create), hence the default of true.
    bool IsChanged = true,
    // R18 (2026-11-03) — the PARENT round's effective qty / delivery date for this line, so a counter round's diff can
    // show what it moved from. Null on round 1 (no parent round).
    decimal? PreviousRoundQty = null,
    DateTime? PreviousRoundDeliveryDate = null);

/// <summary>
/// R18 (2026-11-03) — one round in a negotiation's re-negotiation history: who proposed it, where it stands, and its
/// submit / review audit. Header-level only (no lines); the round's own lines come from its <see cref="PoNegotiationDto"/>.
/// </summary>
public record PoNegotiationRoundDto(
    Guid Id,
    int RoundNo,
    string ProposedBy,
    string NegotiationStatus,
    DateTime SubmittedAt,
    string? SubmittedBy,
    DateTime? ReviewedAt,
    string? ReviewedBy,
    string? Notes,
    string? RejectionReason);

/// <summary>Full negotiation detail: header + delta lines (each carrying the original → negotiated diff).</summary>
public record PoNegotiationDto(
    Guid Id,
    int Seq,
    Guid PurchaseOrderId,
    string PoNumber,
    Guid SupplierId,
    string SupplierName,
    string NegotiationStatus,
    string PreviousPoStatus,
    string? Notes,
    string? RejectionReason,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    string? ReviewedBy,
    DateTime CreatedOn,
    List<PoNegotiationLineDto> Lines,
    // R18 (2026-11-03) — re-negotiation chain. RoundNo / ProposedBy as on the list row; ParentNegotiationId is the round
    // this one countered (null on round 1); HeadNegotiationId is the id of the chain's LATEST round (the one where the
    // next action happens): it equals Id when this round is itself the head, and a superseded (Countered) round points
    // at the head so the UI can link "Superseded by round N"; Rounds is the chain's round history for the timeline
    // (null when the producer does not load it).
    int RoundNo = 1,
    string ProposedBy = "Supplier",
    Guid? ParentNegotiationId = null,
    Guid? HeadNegotiationId = null,
    IReadOnlyList<PoNegotiationRoundDto>? Rounds = null);
