using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Domain.Entities.Doc;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.8 + decision D5, Component 5 (Attachment Requirement Governance). The
/// per-(entity, attachment type) requirement level. Absence of a row = Optional (UC-ATT-06).
///
/// <para><b>Two-tier (D5)</b>: <see cref="SupplierId"/> is NULLABLE. NULL = tenant default; non-NULL = supplier
/// override (supplier wins). Resolution per (entity, type): supplier-row ?? tenant-row ?? Optional. Enforced by
/// two filtered-unique indexes — one on (tenant, entity, type) WHERE supplierId IS NULL, one on
/// (tenant, supplier, entity, type) WHERE supplierId IS NOT NULL.</para>
///
/// Standard aggregate envelope (two-key + audit + seccode + tenant + rowVersion via <see cref="BaseAggregateRoot"/>).
/// </summary>
public class AttachmentRequirementPolicy : BaseAggregateRoot
{
    public Guid AttachmentEntityId { get; set; }
    public AttachmentEntity? AttachmentEntity { get; set; }

    public Guid AttachmentTypeId { get; set; }
    public AttachmentType? AttachmentType { get; set; }

    /// <summary>D5 two-tier: NULL = tenant default; non-NULL = supplier override (supplier wins).</summary>
    public Guid? SupplierId { get; set; }
    public SupplierEntity? Supplier { get; set; }

    public AttachmentRequirement Requirement { get; set; } = AttachmentRequirement.Optional;

    public bool IsActive { get; set; } = true;
}
