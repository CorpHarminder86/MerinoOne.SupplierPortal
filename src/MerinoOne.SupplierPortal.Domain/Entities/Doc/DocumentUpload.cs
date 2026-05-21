using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Doc;

public class DocumentUpload : BaseAggregateRoot
{
    public string OwnerEntityType { get; set; } = string.Empty;
    public Guid OwnerEntityId { get; set; }
    public DocumentType DocumentType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public long FileSizeKb { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public AiValidationStatus AiValidationStatus { get; set; } = AiValidationStatus.Pending;
    public decimal? AiValidationConfidence { get; set; }
    public string? AiValidationPayload { get; set; }
    public DateTime? AiValidatedAt { get; set; }
}
