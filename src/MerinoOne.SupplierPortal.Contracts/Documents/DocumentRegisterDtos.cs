namespace MerinoOne.SupplierPortal.Contracts.Documents;

/// <summary>
/// One row of the cross-module document register (every <c>doc.DocumentUpload</c> the caller may see, RLS-scoped).
/// <see cref="OwnerRef"/> is the human handle of the owning entity (ASN no / invoice no / supplier code / license
/// no), resolved where the caller can also see that owner; null falls back to the short owner id in the UI.
/// <see cref="IdmEntityType"/>/<see cref="Pid"/> surface the Infor IDM sync state (pid set = pushed).
/// </summary>
public record DocumentListItemDto(
    Guid Id,
    int Seq,
    string FileName,
    string DocumentType,
    string OwnerEntityType,
    Guid OwnerEntityId,
    string? OwnerRef,
    long FileSizeKb,
    string MimeType,
    string UploadedBy,
    DateTime CreatedOn,
    string? IdmEntityType,
    string? Pid);
