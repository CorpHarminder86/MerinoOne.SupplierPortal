using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

/// <summary>
/// Post-registration supplier change request (raise → submit → review → approve → apply + push).
/// Modeled as <see cref="BaseAggregateRoot"/> (own seccode + tenant + company): the change-request list
/// and detail screens query this root directly, so it MUST carry seccode RLS — an AuditableEntity here
/// would leak every tenant's change history on a direct DbSet query (verified AppDbContext.ApplyGlobalFilters).
/// Stamp <c>Owner</c> to the supplier's G-seccode on create. <c>RowVersion</c> (via the IHasRowVersion
/// convention) gives optimistic concurrency on approve. R4 (2026-06-22) — Module 2.
/// </summary>
public class SupplierChangeRequest : BaseAggregateRoot
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Lifecycle state. Persisted as the enum name (string), no DB CHECK — the C# enum is the guard.</summary>
    public ChangeRequestStatus ChangeStatus { get; set; } = ChangeRequestStatus.Draft;

    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// The delta lines (one per changed field/entity). Accessed only via this parent — never a root DbSet.
    /// </summary>
    public ICollection<SupplierChangeRequestLine> Lines { get; set; } = new List<SupplierChangeRequestLine>();
}
