using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R5 (TSD R5 Addendum §4.6 / Component 6) — one approval session per Send-for-Approval action on an ASN.
///
/// Lifecycle (§10.1–§10.2):
///   Created at Send-for-Approval: <c>Status = Pending</c>, <c>SubmittedBy/On</c> stamped.
///   Any one mapped PO buyer may:
///     • Approve  → <c>Status = Approved</c>, <c>DecisionBy/On</c> stamped; ASN moves to Submitted.
///     • Reject   → <c>Status = Rejected</c>, <c>DecisionBy/On</c> + <c>Reason</c> stamped (mandatory);
///                  ASN moves to Rejected and is returned to the supplier for edit.
///   An approved ASN re-runs the R4 over-ship guard at final Submit; if the guard fails the approval
///   stands but the Submit is blocked (§10.4 / UC-AP-05).
///
/// Indexes:
///   UX_AsnApproval_asnApprovalSeq — clustered (via ApplyBaseEntityConvention)
///   IX_AsnApproval_asn            — (asnId) — row look-up per ASN
///
/// FK: asnId → proc.Asn CASCADE (approvals are deleted when their ASN is deleted).
/// </summary>
public class AsnApproval : BaseAggregateRoot
{
    /// <summary>FK → proc.Asn. One ASN may have multiple approval sessions (re-raised after rejection).</summary>
    public Guid AsnId { get; set; }
    public Asn? Asn { get; set; }

    /// <summary>
    /// Pending | Approved | Rejected. Persisted as the enum name (string) via HasConversion&lt;string&gt;.
    /// Max 20 chars per §4.6 DDL. No DB CHECK — the C# enum is the guard. APPEND-ONLY.
    /// </summary>
    public AsnApprovalStatus Status { get; set; } = AsnApprovalStatus.Pending;

    /// <summary>The supplier user (userCode / email) who clicked Send-for-Approval.</summary>
    public string SubmittedBy { get; set; } = string.Empty;

    /// <summary>UTC timestamp when Send-for-Approval was executed.</summary>
    public DateTime SubmittedOn { get; set; }

    /// <summary>The buyer who approved or rejected (null while Pending).</summary>
    public string? DecisionBy { get; set; }

    /// <summary>UTC timestamp of the approval or rejection decision (null while Pending).</summary>
    public DateTime? DecisionOn { get; set; }

    /// <summary>
    /// Mandatory on rejection (§10.2); null on approval or while Pending.
    /// The supplier sees this reason on the returned-to-Draft notification.
    /// </summary>
    public string? Reason { get; set; }
}
