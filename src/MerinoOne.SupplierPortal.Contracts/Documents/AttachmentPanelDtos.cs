namespace MerinoOne.SupplierPortal.Contracts.Documents;

/// <summary>
/// R5 (TSD R5 Addendum §13.8) — Component 9, the Policy-Driven Attachment Panel READ-MODEL. One slot per active
/// <c>AttachmentRequirementPolicy</c> type for an (entity, instance), carrying its effective two-tier (D5)
/// requirement badge and ALL uploaded files for that type (§13.4, multiple files per slot). Purely descriptive:
/// no enforcement lives here — the host's submit site (R4 <c>AttachmentSubmitGuard</c>) enforces (§13.6).
///
/// <para>An EMPTY list ⇒ no active policy for the (tenant, entity) ⇒ the host renders no panel (the
/// "no policy → no control" rule, §13.2). Slots are ordered Mandatory → Warning → Optional, then alphabetical
/// by type name (§13.3).</para>
/// </summary>
/// <param name="TypeCode">The <c>AttachmentType.Code</c> — aligns with <c>DocumentUpload.documentType</c>.</param>
/// <param name="TypeName">The <c>AttachmentType.Name</c> display label for the slot.</param>
/// <param name="Requirement">The effective requirement badge: <c>Mandatory</c> | <c>Warning</c> | <c>Optional</c>.</param>
/// <param name="Documents">Every non-deleted file uploaded against this (entity, instance, type) — possibly empty.</param>
public record AttachmentPanelSlotDto(
    string TypeCode,
    string TypeName,
    string Requirement,
    IReadOnlyList<AttachmentPanelDocumentDto> Documents);

/// <summary>
/// R5 (§13.8) — one uploaded file inside a panel slot, projected from <c>doc.DocumentUpload</c>. The download URL
/// is the base-href-relative <c>files/proxy/{id}</c> route (the Web proxy injects bearer auth); mutations
/// (upload / remove) are NOT here — they go through the existing <c>DocumentUploadsController</c>.
/// </summary>
public record AttachmentPanelDocumentDto(
    Guid Id,
    string FileName,
    string? UploadedBy,
    DateTime UploadedOn,
    string DownloadUrl,   // files/proxy/{id}
    string? MimeType,
    long SizeBytes);
