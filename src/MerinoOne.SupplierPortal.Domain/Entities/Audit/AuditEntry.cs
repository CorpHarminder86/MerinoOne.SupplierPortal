using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Audit;

/// <summary>
/// Generic field-level audit ledger row (TSD §7.6).
/// Single table for all entities — partitioned by <see cref="EntityName"/> later if hot.
/// Insert-only: never updated, never deleted. No FKs to source tables (audit must survive
/// deletes). Inherits BaseEntity (two-key pattern + clustered Seq) but NOT AuditableEntity
/// — audit rows are themselves not audited.
/// </summary>
public class AuditEntry : BaseEntity
{
    /// <summary>CLR type name of the audited entity (e.g. "Supplier", "PurchaseOrder").</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Logical Id of the audited entity row.</summary>
    public Guid EntityId { get; set; }

    /// <summary>"Insert" | "Update" | "Delete".</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Property name that changed. Empty string for Insert/Delete summary rows.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Original value (Update/Delete). Null for Insert.</summary>
    public string? OldValue { get; set; }

    /// <summary>New value (Insert/Update). Null for Delete.</summary>
    public string? NewValue { get; set; }

    /// <summary>UserCode of the actor — "system" when unauthenticated.</summary>
    public string ChangedBy { get; set; } = string.Empty;

    /// <summary>UTC timestamp.</summary>
    public DateTime ChangedOn { get; set; } = DateTime.UtcNow;
}
