using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §11.2 — Component 7, the fulfilment-derived statuses). The PURE, DB-less decision for
/// the on-ASN-Submit PO fulfilment-status derivation. Owns ONLY the three-owner milestone table for the
/// <b>portal-derived</b> half of the lifecycle — it is called from <see cref="AsnSubmitExecutor"/> right after
/// the over-ship guard consumes <c>ShippedQtyToDate</c>, in the SAME transaction.
///
/// <para><b>The three milestones, three owners (§11.2):</b></para>
/// <list type="bullet">
///   <item><c>PartiallyDelivered</c> — some PO lines shipped (any line with <c>ShippedQtyToDate &gt; 0</c> but
///         not all fully shipped). Portal-derived.</item>
///   <item><c>FullyShipped</c> — EVERY active line fully shipped (<c>ShippedQtyToDate &gt;= OrderQty</c> for all).
///         Portal-derived; awaits the ERP receipt confirmation.</item>
///   <item><c>Delivered</c> — ERP confirms goods received. ERP-owned (mapped, via <see cref="Integration.Inbound.PoStatusResolver"/>);
///         <b>NEVER</b> set from quantity here. A fully-shipped PO sits at <c>FullyShipped</c> until the ERP
///         <c>Delivered</c> status arrives (UC-SM-08).</item>
/// </list>
///
/// <para><b>Guard — only valid in-fulfilment states are overwritten.</b> The derivation applies ONLY when the PO
/// is currently in an in-fulfilment state where the quantity-derived milestone is meaningful:
/// <c>{ Accepted, Acknowledged, Released, PartiallyDelivered, FullyShipped }</c>. It NEVER touches a terminal /
/// ERP-owned / pre-fulfilment state — <c>Cancelled, Closed, Delivered, Draft, Rejected, Negotiation, Approved</c>
/// — so a routine submit cannot clobber an ERP-owned <c>Delivered</c> nor regress it, and a fully-shipped+delivered
/// PO is left untouched (no <c>FullyShipped</c> regression). It also NEVER emits <c>Delivered</c>: §11.2 reserves
/// that purely for the mapped ERP status.</para>
/// </summary>
public static class FulfilmentStatusDeriver
{
    /// <summary>
    /// The in-fulfilment states in which a quantity-derived milestone may be applied. Anything NOT in this set is
    /// terminal, ERP-owned, or pre-fulfilment and is left strictly untouched.
    /// </summary>
    public static readonly IReadOnlySet<PoStatus> DerivableFrom = new HashSet<PoStatus>
    {
        PoStatus.Accepted,
        PoStatus.Acknowledged,
        PoStatus.Released,
        PoStatus.PartiallyDelivered,
        PoStatus.FullyShipped,
    };

    /// <summary>
    /// True when <paramref name="status"/> is an in-fulfilment state the derivation may overwrite. Pure predicate
    /// equivalent to <c>DerivableFrom.Contains(status)</c>, written as an explicit boolean so it ALSO translates
    /// cleanly to a SQL <c>IN</c> inside an EF <c>Where</c> (the set/array <c>.Contains</c> form binds to the
    /// <c>ReadOnlySpan</c> overload and fails EF translation).
    /// </summary>
    public static bool IsDerivableFrom(PoStatus status) =>
        status == PoStatus.Accepted
        || status == PoStatus.Acknowledged
        || status == PoStatus.Released
        || status == PoStatus.PartiallyDelivered
        || status == PoStatus.FullyShipped;

    /// <summary>
    /// A PO line's shipped-vs-ordered snapshot for the derivation. <paramref name="ShippedQtyToDate"/> is the R4
    /// cumulative cache (post-consumption); <paramref name="OrderQty"/> is the authoritative ordered quantity.
    /// </summary>
    public readonly record struct LineQty(decimal OrderQty, decimal ShippedQtyToDate);

    /// <summary>
    /// Derives the PO's new fulfilment status from its CURRENT status and the post-consumption line quantities
    /// (ALL non-deleted lines of the PO). Returns the status to APPLY, or <c>null</c> to leave <c>PoStatus</c>
    /// unchanged.
    ///
    /// <list type="bullet">
    ///   <item>Current status NOT in <see cref="DerivableFrom"/> (terminal / ERP-owned / pre-fulfilment) → null
    ///         (never clobber).</item>
    ///   <item>No lines → null (nothing to derive).</item>
    ///   <item>EVERY line fully shipped (<c>ShippedQtyToDate &gt;= OrderQty</c>) → <c>FullyShipped</c>
    ///         (never <c>Delivered</c> — that is ERP-owned, §11.2).</item>
    ///   <item>ANY line with <c>ShippedQtyToDate &gt; 0</c> (but not all fully shipped) → <c>PartiallyDelivered</c>.</item>
    ///   <item>No line shipped at all → null (leave unchanged).</item>
    /// </list>
    ///
    /// The return is collapsed to <c>null</c> when it equals <paramref name="currentStatus"/>, so a no-op
    /// (e.g. an already-<c>FullyShipped</c> PO re-deriving to <c>FullyShipped</c>) signals "no change".
    /// </summary>
    public static PoStatus? Derive(PoStatus currentStatus, IReadOnlyCollection<LineQty> lines)
    {
        // 1. Only an in-fulfilment state may be overwritten. Terminal/ERP-owned/pre-fulfilment → never touch.
        if (!IsDerivableFrom(currentStatus))
            return null;

        // 2. Nothing to derive from.
        if (lines.Count == 0)
            return null;

        PoStatus? target;

        // 3. EVERY line fully shipped → FullyShipped. NEVER Delivered (ERP-owned milestone, §11.2 / UC-SM-08).
        if (lines.All(l => l.ShippedQtyToDate >= l.OrderQty))
            target = PoStatus.FullyShipped;
        // 4. ANY line has shipped progress (but not all) → PartiallyDelivered.
        else if (lines.Any(l => l.ShippedQtyToDate > 0m))
            target = PoStatus.PartiallyDelivered;
        // 5. No line shipped at all → leave unchanged.
        else
            target = null;

        // Collapse a no-op to null (e.g. FullyShipped re-deriving to FullyShipped).
        return target == currentStatus ? null : target;
    }
}
