using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

/// <summary>
/// A single delta within a <see cref="SupplierChangeRequest"/> — one row per changed field/entity.
/// Modeled as a plain <see cref="AuditableEntity"/> (child, accessed ONLY via the parent aggregate, never
/// as a root DbSet): it carries no seccode of its own — RLS is enforced on the parent. The audit interceptor
/// supplies the Edit/Delete trail on the applied live row, so only <c>Add</c> ops carry a <c>payloadJson</c>.
/// R4 (2026-06-22) — Module 2.
/// </summary>
public class SupplierChangeRequestLine : AuditableEntity
{
    public Guid SupplierChangeRequestId { get; set; }
    public SupplierChangeRequest? SupplierChangeRequest { get; set; }

    /// <summary>Which supplier sub-entity this delta targets. Persisted as the enum name (string), no DB CHECK.</summary>
    public ChangeTargetEntity TargetEntity { get; set; }

    /// <summary>The existing child row being changed; NULL = Add (no row yet).</summary>
    public Guid? TargetEntityId { get; set; }

    /// <summary>Add | Edit | Delete. Persisted as the enum name (string), no DB CHECK.</summary>
    public ChangeOperation Operation { get; set; }

    public string? FieldName { get; set; }

    /// <summary>Scalar (NOT MAX) — the prior value for an Edit. Not used for Add.</summary>
    public string? OldValue { get; set; }

    /// <summary>Scalar (NOT MAX) — the proposed value for an Edit. Not used for Add.</summary>
    public string? NewValue { get; set; }

    /// <summary>Full proposed row state — <c>Add</c> ops ONLY (no existing row to diff). Edit/Delete rely on the audit trail.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Per-line ERP push state. Persisted as the enum name (string), no DB CHECK.</summary>
    public LinePushStatus PushStatus { get; set; } = LinePushStatus.Pending;

    public DateTime? PushedAt { get; set; }

    /// <summary>The LN handle for this line, filled later by the /inbound/erp-ack endpoint.</summary>
    public string? ErpRef { get; set; }
}
