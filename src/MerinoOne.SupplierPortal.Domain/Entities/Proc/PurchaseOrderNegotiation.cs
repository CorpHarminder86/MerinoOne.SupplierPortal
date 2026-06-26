using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R4 (2026-06-24) — PO Negotiation (mirrors SupplierChangeRequest end-to-end). A supplier raises a negotiation
/// on a Released/Acknowledged PO proposing revised qty / delivery dates; the buyer approves or rejects it.
/// Modeled as <see cref="BaseAggregateRoot"/> (own seccode + tenant + company): the negotiation list and detail
/// screens query this root directly, so it MUST carry seccode RLS — an AuditableEntity here would leak every
/// tenant's negotiation history on a direct DbSet query (verified AppDbContext.ApplyGlobalFilters). Stamp
/// <c>Owner</c> to the supplier's G-seccode on create. <c>RowVersion</c> (via the IHasRowVersion convention)
/// gives optimistic concurrency on approve (concurrent approve → DbUpdateConcurrencyException → 409).
/// On buyer approval the negotiated terms are pushed to ERP (outbox) and the local <c>PurchaseOrderLine</c>
/// rows are NOT mutated — ERP re-syncs the revised PO inbound (preserves the ERP-master / read-only-line invariant).
/// </summary>
public class PurchaseOrderNegotiation : BaseAggregateRoot
{
    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }

    /// <summary>Denormalized PO number — used for the outbox key + display without a join.</summary>
    public string PoNumber { get; set; } = string.Empty;

    public Guid SupplierId { get; set; }

    /// <summary>Lifecycle state. Persisted as the enum name (string), no DB CHECK — the C# enum is the guard.</summary>
    public PoNegotiationStatus NegotiationStatus { get; set; } = PoNegotiationStatus.Submitted;

    /// <summary>
    /// The PO status captured at create — the revert target for cancel/reject (avoid hardcoding Acknowledged).
    /// Persisted as the enum name (string), no DB CHECK.
    /// </summary>
    public PoStatus PreviousPoStatus { get; set; }

    public DateTime SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }

    /// <summary>
    /// R4 (2026-06-26) — Phase 6 / UC-PO-04. Dedupe stamp for the 48h negotiation-SLA buyer nudge. NULL until the
    /// <c>SlaNudgeWorker</c> BackgroundService enqueues the one-time "negotiation awaiting your review" reminder to the
    /// buyer (resolved via the PO's <c>BuyerUserId</c>); stamped UTC-now on enqueue so the worker never nudges the same
    /// Submitted negotiation twice. Reset implicitly when a new negotiation row is raised (each negotiation is its own row).
    /// </summary>
    public DateTime? NudgeSentAt { get; set; }

    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// The changed (delta) lines — only lines whose qty/delivery date differ from the PO are persisted, each
    /// carrying the original snapshot for the buyer diff view. Accessed only via this parent — never a root DbSet.
    /// </summary>
    public ICollection<PurchaseOrderNegotiationLine> Lines { get; set; } = new List<PurchaseOrderNegotiationLine>();
}
