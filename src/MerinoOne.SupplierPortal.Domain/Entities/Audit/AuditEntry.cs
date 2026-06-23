using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Audit;

/// <summary>
/// Generic field-level audit ledger row (TSD §7.6).
/// Single table for all entities — partitioned by <see cref="EntityName"/> later if hot.
/// Insert-only: never updated, never deleted. No FKs to source tables (audit must survive
/// deletes). Inherits BaseEntity (two-key pattern + clustered Seq) but NOT AuditableEntity
/// — audit rows are themselves not audited.
///
/// Implements <see cref="ITenantOwned"/> purely to declare its <see cref="TenantId"/>; it is NOT
/// pulled into the always-on tenant-filter loop (that loop keys on <see cref="ISoftDelete"/>, which
/// AuditEntry deliberately does not implement). A dedicated, soft-delete-free tenant query filter is
/// attached explicitly in <c>AppDbContext.ApplyGlobalFilters</c> so reads are fail-closed tenant-scoped.
/// </summary>
public class AuditEntry : BaseEntity, ITenantOwned
{
    /// <summary>
    /// Owning tenant of the audited row. Stamped by <c>AuditableEntityInterceptor</c> from the audited
    /// entity's own TenantId when it carries one, else the current principal's TenantId, else null.
    /// Legacy rows written before migration 0025 stay null and are visible only to bypass principals.
    /// </summary>
    public Guid? TenantId { get; set; }

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
