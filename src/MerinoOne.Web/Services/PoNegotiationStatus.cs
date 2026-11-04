namespace MerinoOne.Web.Services;

/// <summary>
/// Presentation helpers for the R4 PO Negotiation lifecycle. Mirrors <see cref="SupplierChangeStatus"/>.
/// The backend serializes the NegotiationStatus enum as its NAME (string); this centralises the badge
/// colour + human label mapping so the supplier "PO Change Request" list, the buyer review queue and the
/// buyer diff/detail view stay visually consistent.
///
/// NegotiationStatus ∈ { Submitted, Approved, Rejected, Cancelled, Countered }.
///
/// R18 (2026-11-04) — a negotiation is a chain of rounds: the supplier opens round 1, then each side may answer the
/// other's open round with a counter (a NEW round; the answered one becomes Countered) or turn it down / accept it.
/// Submitted still marks the one OPEN round, but whose move it is depends on who proposed it (ProposedBy, "Supplier" |
/// "Buyer"): the OTHER party acts. <see cref="AwaitingParty"/>, <see cref="IsBuyerActionable"/> and
/// <see cref="IsSupplierActionable"/> answer that; <see cref="IsReviewable"/> and <see cref="IsEditable"/> only say the
/// round is open.
/// </summary>
public static class PoNegotiationStatus
{
    /// <summary>Maps a NegotiationStatus enum name to a catalog-list status badge class (tlg-status--*).</summary>
    public static string CssClass(string status) => status switch
    {
        "Approved"            => "tlg-status--active",
        "Submitted"           => "tlg-status--pending",
        "Rejected"            => "tlg-status--danger",
        "Cancelled"           => "tlg-status--off",
        "Countered"           => "tlg-status--info",
        _                     => "tlg-status--info",
    };

    /// <summary>Human-friendly label for a NegotiationStatus (the enum names are already single words).</summary>
    public static string Label(string status) => status switch
    {
        _ => status,
    };

    /// <summary>
    /// Submitted is the only state a round can be answered in. Round-agnostic: it says the round is OPEN, not that the
    /// caller's side is the one to answer it — use <see cref="IsBuyerActionable"/> / <see cref="IsSupplierActionable"/>
    /// to decide which party's actions to offer.
    /// </summary>
    public static bool IsReviewable(string status) => status is "Submitted";

    /// <summary>
    /// Submitted is the only "open" state — while a negotiation is open the PO is locked in <c>Negotiation</c>. (No
    /// supplier-side editing of an already-submitted round; the editable line grid lives on the PO detail before
    /// submit, and a change after that is a counter, which opens a new round.) Like <see cref="IsReviewable"/> this
    /// ignores who proposed the round: the supplier may cancel only a round it proposed itself, not a buyer's counter.
    /// </summary>
    public static bool IsEditable(string status) => status is "Submitted";

    /// <summary>
    /// R18 (2026-11-04) — the party whose move it is on an open round: the one that did NOT propose it. "Buyer" for a
    /// supplier's round, "Supplier" for a buyer's counter; null once the round is no longer open (Approved, Rejected,
    /// Cancelled, Countered) or when <paramref name="proposedBy"/> is neither side.
    /// </summary>
    public static string? AwaitingParty(string status, string proposedBy) => !IsReviewable(status)
        ? null
        : proposedBy switch
        {
            "Supplier" => "Buyer",
            "Buyer"    => "Supplier",
            _          => null,
        };

    /// <summary>R18 (2026-11-04) — true when the buyer may approve, reject or counter: an open round the supplier proposed.</summary>
    public static bool IsBuyerActionable(string status, string proposedBy) => AwaitingParty(status, proposedBy) == "Buyer";

    /// <summary>R18 (2026-11-04) — true when the supplier may accept, decline or counter: an open round the buyer proposed.</summary>
    public static bool IsSupplierActionable(string status, string proposedBy) => AwaitingParty(status, proposedBy) == "Supplier";
}
