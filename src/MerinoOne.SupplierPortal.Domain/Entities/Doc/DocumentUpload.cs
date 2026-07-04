using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Doc;

public class DocumentUpload : BaseAggregateRoot
{
    public string OwnerEntityType { get; set; } = string.Empty;
    public Guid OwnerEntityId { get; set; }

    /// <summary>
    /// R4 (2026-06-26) — TSD R4 Addendum §3.6, Component 5. The attachment-type CODE. Was the fixed
    /// <c>DocumentType</c> enum (stored as its NAME via <c>HasConversion&lt;string&gt;()</c>); migrated to a plain
    /// string so admin-added <c>doc.AttachmentType</c> master codes (not in the legacy enum) are storable end-to-end.
    /// The DB column is unchanged (<c>nvarchar(50)</c>) — this is a ZERO-migration CLR-representation change. Legacy
    /// values keep their enum-name strings ("Invoice", "License", "AsnAttachment", "OnboardingPan", …); the
    /// well-known names live on <see cref="MerinoOne.SupplierPortal.Domain.Enums.DocumentType"/> for the
    /// onboarding/license code paths that still reference specific members.
    /// </summary>
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public long FileSizeKb { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public AiValidationStatus AiValidationStatus { get; set; } = AiValidationStatus.Pending;
    public decimal? AiValidationConfidence { get; set; }
    public string? AiValidationPayload { get; set; }
    public DateTime? AiValidatedAt { get; set; }

    // R8 (2026-07-04) — TSD R8 §3.2 / D-R8-15. IDM outbound sync discriminator + durable handle.
    // IdmEntityType selects the per-type config (also emitted as the MDS_EntityType payload value); it is
    // backfillable and stamped by the outbox seeding scan from the documentType→config mapping. Pid is the
    // IDM item id returned on the first successful Create — NULL until then, and the mutation handle for
    // subsequent Update/Delete.
    public string? IdmEntityType { get; set; }
    public string? Pid { get; set; }
}
