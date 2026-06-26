using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Doc;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.7, Component 5 (Attachment Requirement Governance). Reference master
/// enumerating the transactions for which attachment requirements may be configured. Seeded with Supplier,
/// Asn, Invoice; extensible later. <see cref="Code"/> aligns with <c>DocumentUpload.ownerEntityType</c>.
/// Standard aggregate envelope (two-key + audit + seccode + tenant + rowVersion via <see cref="BaseAggregateRoot"/>).
/// </summary>
public class AttachmentEntity : BaseAggregateRoot
{
    /// <summary>Stable code aligned with <c>DocumentUpload.ownerEntityType</c>: "Supplier" | "Asn" | "Invoice".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display label, e.g. "Advance Shipping Notice".</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
