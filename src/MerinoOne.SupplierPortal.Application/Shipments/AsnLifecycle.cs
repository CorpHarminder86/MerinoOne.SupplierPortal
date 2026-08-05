using MerinoOne.SupplierPortal.Domain.Enums;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §10.1) — the single guarded transition table for the ASN lifecycle. Every ASN command
/// asserts its legal from-state through here BEFORE mutating, so the legal transitions live in ONE place and an
/// illegal transition always surfaces the same <see cref="ConflictException"/> (mapped to 409).
///
/// <para>Lifecycle — R14 splits the buyer's decision from the ERP dispatch. Approve no longer submits; the
/// supplier's POST does. Which path applies is decided per supplier by <c>Supplier.AsnConfirmationRequired</c>:
/// <code>
/// required = true:
///   Draft ──(SendForApproval)──▶ PendingApproval ──(Approve)──▶ Approved ──(Post)──▶ Submitted ──▶ InTransit ──▶ Delivered
///                                     │                            │
///                                     │                            └─ supplier edits + uploads documents here
///                                     └──(Reject + reason)──▶ Rejected ──(supplier edits)──▶ Draft
///
/// required = false:
///   Draft ──────────────────────────────(Post)──────────────────────────▶ Submitted ──▶ InTransit ──▶ Delivered
///
/// Any active state ──(Cancel)──▶ Cancelled
/// </code>
/// </para>
///
/// <para><see cref="Asn.AsnStatus"/> remains the SINGLE lifecycle source of truth; the
/// <see cref="Domain.Entities.Proc.AsnApproval"/> session row is kept in lockstep transactionally by the
/// approval handlers (it never drives the ASN state independently).</para>
/// </summary>
public static class AsnLifecycle
{
    /// <summary>
    /// States an ASN may still be Cancelled from (§10.1 "any active state"). R14 adds
    /// <see cref="AsnStatus.Approved"/> — a confirmed-but-unposted ASN is very much active, and the supplier
    /// must be able to withdraw it (nothing has been consumed or dispatched yet).
    /// </summary>
    public static readonly IReadOnlySet<AsnStatus> Cancellable = new HashSet<AsnStatus>
    {
        AsnStatus.Draft, AsnStatus.PendingApproval, AsnStatus.Approved, AsnStatus.Rejected,
        AsnStatus.Submitted, AsnStatus.InTransit,
    };

    /// <summary>Asserts the ASN is in <paramref name="expected"/>; else 409 naming the action + actual state.</summary>
    public static void AssertFrom(AsnStatus actual, AsnStatus expected, string action)
    {
        if (actual != expected)
            throw new ConflictException(
                $"Cannot {action}: ASN is '{actual}', expected '{expected}'.");
    }

    /// <summary>Send for Approval: Draft → PendingApproval.</summary>
    public static void AssertCanSendForApproval(AsnStatus actual)
        => AssertFrom(actual, AsnStatus.Draft, "send for approval");

    /// <summary>
    /// Approve (buyer): PendingApproval → Approved. R14 — this NO LONGER runs the submit path; it only records
    /// the buyer's confirmation. The ERP dispatch happens later, at the supplier's Post.
    /// </summary>
    public static void AssertCanApprove(AsnStatus actual)
        => AssertFrom(actual, AsnStatus.PendingApproval, "approve");

    /// <summary>Reject (buyer): PendingApproval → Rejected.</summary>
    public static void AssertCanReject(AsnStatus actual)
        => AssertFrom(actual, AsnStatus.PendingApproval, "reject");

    /// <summary>
    /// Supplier edit (Update): allowed in Draft, Rejected, OR Approved. R14 (D5) opens the Approved state for
    /// editing so the supplier can complete the shipment references and upload the Packing List / Invoice after
    /// the buyer's confirmation — the whole point of the confirmation flow. Quantities can still never exceed the
    /// PO balance: the atomic over-ship guard runs at Post, after any such edit.
    /// </summary>
    public static void AssertCanEdit(AsnStatus actual)
    {
        if (actual is not (AsnStatus.Draft or AsnStatus.Rejected or AsnStatus.Approved))
            throw new ConflictException(
                $"Cannot edit: ASN is '{actual}'; only a Draft, Rejected or Approved ASN can be edited.");
    }

    /// <summary>
    /// R14 — Post (supplier): the ERP dispatch trigger, and the only transition that reaches
    /// <c>AsnSubmitExecutor</c>. Legal from-states depend on the supplier's confirmation mode:
    /// <list type="bullet">
    ///   <item><b>Approved → Submitted</b> in BOTH modes. Deliberately not gated on
    ///         <paramref name="confirmationRequired"/>: flipping a supplier to No-mode while one of its ASNs sits
    ///         in Approved must not strand that ASN, and No-mode has no approval path to send it back through.</item>
    ///   <item><b>Draft → Submitted</b> only when confirmation is NOT required (there is no buyer to confirm).</item>
    /// </list>
    /// </summary>
    public static void AssertCanPost(AsnStatus actual, bool confirmationRequired)
    {
        if (actual == AsnStatus.Approved) return;
        if (!confirmationRequired && actual == AsnStatus.Draft) return;

        throw new ConflictException(confirmationRequired
            ? $"Cannot post: ASN is '{actual}'. This supplier requires buyer confirmation, so only an Approved ASN can be posted."
            : $"Cannot post: ASN is '{actual}'. This supplier does not require buyer confirmation, so only a Draft or Approved ASN can be posted.");
    }

    /// <summary>Cancel: any active state → Cancelled (the single-cancel guard against a terminal ASN).</summary>
    public static void AssertCanCancel(AsnStatus actual)
    {
        if (!Cancellable.Contains(actual))
            throw new ConflictException(
                $"Cannot cancel: ASN is '{actual}'; only an active (non-terminal) ASN can be cancelled.");
    }
}
