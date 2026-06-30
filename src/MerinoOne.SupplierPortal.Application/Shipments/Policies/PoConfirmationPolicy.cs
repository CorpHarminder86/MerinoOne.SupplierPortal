using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Shipments.Policies;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §6.2 (Component 3, PO Confirmation Gate). Pure, trivially unit-testable
/// state→gate lookup: given the PO's lifecycle status and the supplier's confirmation mode, does the gate ALLOW
/// a new/draft ASN to be created (and a draft submitted)?
///
/// <para>The matrix (§6.2), encoded below. D2 — <see cref="PoStatus.Negotiation"/> and <see cref="PoStatus.Approved"/>
/// BLOCK in every mode (they replace the removed <c>DateProposed</c> row: terms are being contested / an ERP
/// re-sync is pending, so shipping is frozen). Everything not explicitly allowed below is blocked
/// (Draft / Rejected / Cancelled / Closed / Negotiation / Approved, plus Delivered which is balance-driven to a
/// no-op).</para>
///
/// <list type="bullet">
///   <item><c>AutoAccept</c>      → Released, Acknowledged, Accepted, PartiallyDelivered, FullyShipped.</item>
///   <item><c>AcknowledgeToShip</c> → Acknowledged, Accepted, PartiallyDelivered, FullyShipped.</item>
///   <item><c>AcceptToShip</c>     → Accepted, PartiallyDelivered, FullyShipped.</item>
/// </list>
///
/// <para>R5 (§11.2 gate note) — <see cref="PoStatus.FullyShipped"/> is balance-driven like
/// <see cref="PoStatus.PartiallyDelivered"/>: it is shippable at the gate, with the at-Submit over-ship guard
/// (balance = 0) blocking any further ASN. No extra create-gate blocking is added for it.</para>
/// </summary>
public static class PoConfirmationPolicy
{
    /// <summary>
    /// True when shipping is permitted for the given (status, mode); false = gate BLOCKS. Enforced at
    /// <c>CreateAsnCommand</c> (new/draft) and <c>SubmitAsnCommand</c> (draft → submitted), per covered PO.
    /// </summary>
    public static bool AllowsShipping(PoStatus status, PoConfirmationMode mode) => status switch
    {
        // PartiallyDelivered + Accepted unblock in EVERY mode (the line is already confirmed-and-shipping).
        PoStatus.Accepted           => true,
        PoStatus.PartiallyDelivered => true,

        // R5 (§11.2 gate note) — FullyShipped is balance-driven like PartiallyDelivered: the PO is already
        // confirmed-and-shipping, so the CONFIRMATION gate must NOT block here. There is no remaining balance, so
        // the authoritative over-ship guard at Submit (orderQty×factor − shippedQtyToDate ≥ shipQty, balance = 0)
        // is what blocks any further ASN — exactly as §11.2 prescribes ("no extra gating work"). Blocking at the
        // create gate instead would change the failure point and break the at-Submit balance-guard contract.
        PoStatus.FullyShipped       => true,

        // Acknowledged unblocks for AutoAccept + AcknowledgeToShip; AcceptToShip still requires an Accept.
        PoStatus.Acknowledged => mode is PoConfirmationMode.AutoAccept or PoConfirmationMode.AcknowledgeToShip,

        // Released unblocks ONLY for AutoAccept (auto-stamped Accepted at release anyway — but a re-released PO
        // that has not yet been auto-stamped is still shippable under AutoAccept per §6.2).
        PoStatus.Released => mode is PoConfirmationMode.AutoAccept,

        // D2 / §6.2 — Negotiation + Approved BLOCK in all modes (replace the removed DateProposed row); everything
        // else (Draft / Rejected / Cancelled / Closed / Delivered) blocks too.
        _ => false,
    };

    /// <summary>
    /// The supplier-facing action the gate is waiting on, for the block message (e.g. "Accept" / "Acknowledge").
    /// AutoAccept never blocks for "Acknowledge"/"Accept" reasons (it auto-stamps), so it falls through to "Accept"
    /// for any non-shippable terminal/contested status.
    /// </summary>
    public static string RequiredAction(PoConfirmationMode mode) => mode switch
    {
        PoConfirmationMode.AcknowledgeToShip => "Acknowledge",
        _ => "Accept",
    };
}
