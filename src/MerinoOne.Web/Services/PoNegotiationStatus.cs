namespace MerinoOne.Web.Services;

/// <summary>
/// Presentation helpers for the R4 PO Negotiation lifecycle. Mirrors <see cref="SupplierChangeStatus"/>.
/// The backend serializes the NegotiationStatus enum as its NAME (string); this centralises the badge
/// colour + human label mapping so the supplier "PO Change Request" list, the buyer review queue and the
/// buyer diff/detail view stay visually consistent.
///
/// NegotiationStatus ∈ { Submitted, Approved, Rejected, Cancelled }.
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
        _                     => "tlg-status--info",
    };

    /// <summary>Human-friendly label for a NegotiationStatus (the enum names are already single words).</summary>
    public static string Label(string status) => status switch
    {
        _ => status,
    };

    /// <summary>Submitted is the only state a buyer may approve / reject.</summary>
    public static bool IsReviewable(string status) => status is "Submitted";

    /// <summary>
    /// Submitted is the only "open" state — while a negotiation is open the supplier may cancel it and the
    /// PO is locked in <c>Negotiation</c>. (No supplier-side editing of an already-submitted negotiation;
    /// the editable line grid lives on the PO detail before submit.)
    /// </summary>
    public static bool IsEditable(string status) => status is "Submitted";
}
