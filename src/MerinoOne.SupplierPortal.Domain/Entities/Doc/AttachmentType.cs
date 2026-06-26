using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Doc;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.6, Component 5 (Attachment Requirement Governance). Configurable,
/// tenant-scoped attachment-type catalogue that supersedes the fixed <c>DocumentType</c> enum as the
/// authoritative source. <see cref="Code"/> aligns with <c>DocumentUpload.DocumentType</c> values so the upload
/// path can validate against active master codes (migrated in Phase 4). Standard aggregate envelope
/// (two-key + audit + seccode + tenant + rowVersion via <see cref="BaseAggregateRoot"/>).
/// </summary>
public class AttachmentType : BaseAggregateRoot
{
    /// <summary>Stable code aligned with <c>DocumentUpload.documentType</c>, e.g. "TestCertificate".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display label, e.g. "Test Certificate".</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
