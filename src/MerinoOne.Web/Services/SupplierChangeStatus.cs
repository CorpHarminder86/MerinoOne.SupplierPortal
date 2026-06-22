namespace MerinoOne.Web.Services;

/// <summary>
/// Presentation helpers for the R4 Module 2 supplier change-request lifecycle. The backend serializes the
/// ChangeStatus / PushStatus enums as their NAME (string); this centralises the badge colour + human label
/// mapping so the supplier list, internal list and detail/diff views stay visually consistent.
///
/// ChangeStatus ∈ { Draft, Submitted, UnderReview, ChangesRequested, Approved, Rejected, Pushed,
///                  PartiallyPushed, PushFailed }.
/// PushStatus   ∈ { Pending, Pushed, PushFailed }.
/// </summary>
public static class SupplierChangeStatus
{
    /// <summary>Maps a ChangeStatus enum name to a catalog-list status badge class (tlg-status--*).</summary>
    public static string CssClass(string status) => status switch
    {
        "Approved" or "Pushed"                 => "tlg-status--active",
        "Submitted" or "UnderReview"           => "tlg-status--pending",
        "ChangesRequested" or "PartiallyPushed" => "tlg-status--pending",
        "Rejected" or "PushFailed"             => "tlg-status--danger",
        "Draft"                                => "tlg-status--off",
        _                                       => "tlg-status--info",
    };

    /// <summary>Human-friendly label for a ChangeStatus (splits the PascalCase enum names).</summary>
    public static string Label(string status) => status switch
    {
        "UnderReview"      => "Under review",
        "ChangesRequested" => "Changes requested",
        "PartiallyPushed"  => "Partially pushed",
        "PushFailed"       => "Push failed",
        _                  => status,
    };

    /// <summary>Per-line ERP push-status badge class: Pending=grey, Pushed=green, PushFailed=red.</summary>
    public static string PushCssClass(string pushStatus) => pushStatus switch
    {
        "Pushed"     => "tlg-status--active",
        "PushFailed" => "tlg-status--danger",
        _            => "tlg-status--off",
    };

    public static string PushLabel(string pushStatus) => pushStatus switch
    {
        "PushFailed" => "Push failed",
        _            => pushStatus,
    };

    /// <summary>Draft / ChangesRequested are the only states the supplier may still edit + (re)submit.</summary>
    public static bool IsEditable(string status) => status is "Draft" or "ChangesRequested";

    /// <summary>Submitted / UnderReview are the only states a reviewer may approve / reject / bounce back.</summary>
    public static bool IsReviewable(string status) => status is "Submitted" or "UnderReview";
}
