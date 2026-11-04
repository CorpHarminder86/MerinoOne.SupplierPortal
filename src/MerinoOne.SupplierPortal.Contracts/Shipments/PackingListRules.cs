namespace MerinoOne.SupplierPortal.Contracts.Shipments;

/// <summary>
/// R18 (2026-11-03) — the one rule for when an ASN's Packing List may be printed, shared by the API (409 guard) and the
/// Web (button visibility) so the two can never disagree. Contracts cannot reference the Domain enum, so the status is
/// the AsnStatus enum NAME the ASN DTOs carry as a string.
///
/// <para>Buyer-approval suppliers (<c>asnConfirmationRequired</c>) print from the buyer's approval onward (Approved, then
/// the posted statuses); no-approval suppliers print from Draft, i.e. before Post. Cancelled, PendingApproval and
/// Rejected are never available.</para>
/// </summary>
public static class PackingListRules
{
    /// <summary>
    /// True when the Packing List is available for an ASN in <paramref name="asnStatus"/> under its supplier's
    /// <paramref name="asnConfirmationRequired"/> mode. Matching is ordinal (case-sensitive); a null, blank or unknown
    /// status is not available.
    /// </summary>
    public static bool IsAvailable(string asnStatus, bool asnConfirmationRequired) => asnStatus switch
    {
        "Submitted" or "InTransit" or "Delivered" or "Received" => true,
        "Approved" => asnConfirmationRequired,
        "Draft" => !asnConfirmationRequired,
        _ => false
    };
}
