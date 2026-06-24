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
// ============================================================================================

// ---------------- Request bodies ----------------

/// <summary>
/// A single proposed line delta in a create request. Only lines whose qty OR delivery date actually
/// differ from the live PO line are persisted (the handler drops no-op lines).
/// </summary>
public record PoNegotiationLineInput(
    Guid PurchaseOrderLineId,
    decimal NegotiatedQty,
    DateTime? NegotiatedDeliveryDate);

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

// ---------------- Read models ----------------

/// <summary>One row in the negotiation list grid (supplier's own / buyer review queue).</summary>
public record PoNegotiationListItemDto(
    Guid Id,
    string PoNumber,
    string SupplierName,
    string NegotiationStatus,
    int LineCount,
    DateTime SubmittedAt);

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
    DateTime? NegotiatedDeliveryDate);

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
    List<PoNegotiationLineDto> Lines);
