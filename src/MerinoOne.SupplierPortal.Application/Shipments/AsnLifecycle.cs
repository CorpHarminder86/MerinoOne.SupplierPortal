using MerinoOne.SupplierPortal.Domain.Enums;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §10.1) — the single guarded transition table for the ASN lifecycle. Every ASN command
/// asserts its legal from-state through here BEFORE mutating, so the legal transitions live in ONE place and an
/// illegal transition always surfaces the same <see cref="ConflictException"/> (mapped to 409).
///
/// <para>Lifecycle:
/// <code>
/// Draft ──(SendForApproval)──▶ PendingApproval ──(Approve)──▶ Submitted ──▶ InTransit ──▶ Delivered
///                                   │
///                                   └──(Reject + reason)──▶ Rejected ──(supplier edits)──▶ Draft
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
    /// <summary>States an ASN may still be Cancelled from (§10.1 "any active state").</summary>
    public static readonly IReadOnlySet<AsnStatus> Cancellable = new HashSet<AsnStatus>
    {
        AsnStatus.Draft, AsnStatus.PendingApproval, AsnStatus.Rejected,
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

    /// <summary>Approve (buyer): PendingApproval → Submitted (runs the submit path).</summary>
    public static void AssertCanApprove(AsnStatus actual)
        => AssertFrom(actual, AsnStatus.PendingApproval, "approve");

    /// <summary>Reject (buyer): PendingApproval → Rejected.</summary>
    public static void AssertCanReject(AsnStatus actual)
        => AssertFrom(actual, AsnStatus.PendingApproval, "reject");

    /// <summary>Supplier edit (Update): allowed in Draft OR Rejected (a rejected ASN returns to edit, §10.1).</summary>
    public static void AssertCanEdit(AsnStatus actual)
    {
        if (actual is not (AsnStatus.Draft or AsnStatus.Rejected))
            throw new ConflictException(
                $"Cannot edit: ASN is '{actual}'; only a Draft or Rejected ASN can be edited.");
    }

    /// <summary>Cancel: any active state → Cancelled (the single-cancel guard against a terminal ASN).</summary>
    public static void AssertCanCancel(AsnStatus actual)
    {
        if (!Cancellable.Contains(actual))
            throw new ConflictException(
                $"Cannot cancel: ASN is '{actual}'; only an active (non-terminal) ASN can be cancelled.");
    }
}
