using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Route("api/document-uploads")]
public class DocumentUploadsController : ControllerBase
{
    /// <summary>5 MB cap mirrors Register.razor's MaxFileBytes.</summary>
    private const long MaxBytes = 5L * 1024 * 1024;

    /// <summary>Marker OwnerEntityType used while a doc is bound to a pre-submission invite.</summary>
    public const string PendingInviteOwnerType = "PendingInvite";

    private readonly IAppDbContext _db;
    private readonly IFileStorageService _storage;

    public DocumentUploadsController(IAppDbContext db, IFileStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    /// <summary>
    /// Anonymous upload bound to a pending supplier invite via its token. Caps file size at 5 MB.
    /// Persists a <see cref="DocumentUpload"/> row with <c>OwnerEntityType="PendingInvite"</c> +
    /// <c>OwnerEntityId=invite.Id</c>; the RegisterSupplierCommand handler rewrites ownership to
    /// the new supplier when the invite is consumed.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBytes + 4096)]
    [EndpointSummary("Upload onboarding document (anonymous, token-scoped)")]
    public async Task<Result<UploadedDocumentDto>> Upload(
        [FromForm] IFormFile file,
        [FromForm] string documentType,
        [FromForm] string token,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Result<UploadedDocumentDto>.Fail("File is required.");
        if (file.Length > MaxBytes)
            return Result<UploadedDocumentDto>.Fail($"File exceeds {MaxBytes / 1024 / 1024} MB.");
        if (!Enum.TryParse<DocumentType>(documentType, true, out var docType))
            return Result<UploadedDocumentDto>.Fail($"Unknown documentType '{documentType}'.");

        var now = DateTime.UtcNow;
        var invite = await _db.SupplierInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Token == token, ct);
        if (invite is null)
            return Result<UploadedDocumentDto>.Fail("Invite token not found.");
        if (invite.ConsumedAt.HasValue || invite.CancelledAt.HasValue || invite.ExpiresAt < now)
            return Result<UploadedDocumentDto>.Fail("Invite is no longer valid for uploads.");

        await using var src = file.OpenReadStream();
        var stored = await _storage.StoreAsync(src, file.FileName, file.ContentType ?? "application/octet-stream", invite.Id, ct);

        // DocumentUpload requires a non-null FK Seccode (BaseAggregateRoot.SeccodeId is Guid).
        // No system-wide seccode exists yet — create a throwaway Group seccode per invite on
        // first upload, then reuse it for subsequent uploads against the same invite. The
        // RegisterSupplierCommand handler rewrites DocumentUpload.SeccodeId to the supplier's
        // seccode on consume; the orphan invite seccode is left in place (negligible row count).
        var pendingSeccodeId = await _db.Seccodes
            .Where(s => s.Name == $"invite-pending-{invite.Id:N}")
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (!pendingSeccodeId.HasValue)
        {
            pendingSeccodeId = Guid.NewGuid();
            _db.Seccodes.Add(new Seccode
            {
                Id = pendingSeccodeId.Value,
                SeccodeType = SeccodeType.G,
                Name = $"invite-pending-{invite.Id:N}",
                CreatedBy = "self-registration",
                CreatedOn = now,
            });
        }

        var docId = Guid.NewGuid();
        var sizeKb = (int)Math.Ceiling(stored.SizeBytes / 1024d);
        _db.DocumentUploads.Add(new DocumentUpload
        {
            Id = docId,
            OwnerEntityType = PendingInviteOwnerType,
            OwnerEntityId = invite.Id,
            DocumentType = docType,
            FileName = file.FileName,
            FileUrl = stored.StorageKey,
            FileSizeKb = sizeKb,
            MimeType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            UploadedBy = "self-registration",
            SeccodeId = pendingSeccodeId.Value,
            AiValidationStatus = AiValidationStatus.Pending,
            CreatedBy = "self-registration",
            CreatedOn = now,
        });
        await _db.SaveChangesAsync(ct);

        var dto = new UploadedDocumentDto(
            docId, docType.ToString(), file.FileName, sizeKb,
            string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            // Base-href-relative (NO leading slash) so the browser resolves against the Web
            // app's <base href> — works both at root ("/") and at sub-path ("/sup-dev/").
            $"files/proxy/{docId}/by-token/{token}");
        return Result<UploadedDocumentDto>.Ok(dto, HttpContext.TraceIdentifier);
    }

    // ============================================================================================
    // R4 (2026-06-22) — Authenticated attachment endpoints. Used by logged-in staff/suppliers to
    // attach files to a saved SupplierLicense (or stage files before first save, then rebind via
    // AddSupplierLicenseCommand/UpdateSupplierLicenseCommand). Permission: Supplier.Write; row access
    // enforced with SupplierWriteGuard against the supplier's G-seccode. The anonymous invite-upload
    // flow above is untouched.
    // ============================================================================================

    /// <summary>
    /// Authenticated attachment upload. Stores the file via <see cref="IFileStorageService"/> (same 5 MB cap
    /// as the anonymous path) and persists a <see cref="DocumentUpload"/> stamped with the supplier's
    /// G-seccode. Owner modes:
    /// <list type="bullet">
    ///   <item><c>ownerEntityType="SupplierLicense"</c>, <c>ownerEntityId=&lt;licenseId&gt;</c> — direct attach to a
    ///         saved license (the license must belong to <c>supplierId</c>); DocumentType.License.</item>
    ///   <item><c>ownerEntityType="Asn"</c>, <c>ownerEntityId=&lt;asnId&gt;</c> — direct attach to a DRAFT ASN (the
    ///         ASN must belong to <c>supplierId</c> and be Draft — attach is LOCKED once Submitted); DocumentType.AsnAttachment.</item>
    ///   <item><c>ownerEntityType="Staging"</c>, <c>ownerEntityId=&lt;clientDraftGuid&gt;</c> — deferred upload before
    ///         the owner is first saved; the owning command (license / ASN) re-points the row on save.</item>
    /// </list>
    /// Write access is enforced via <see cref="SupplierWriteGuard"/> on <paramref name="supplierId"/>'s seccode.
    /// </summary>
    [HttpPost("attach")]
    [Authorize(Policy = "Supplier.Write")]
    [RequestSizeLimit(MaxBytes + 4096)]
    [EndpointSummary("Attach a document to a supplier license or ASN (authenticated)")]
    [EndpointDescription(@"Uploads a file and binds it to a SupplierLicense (ownerEntityType='SupplierLicense'), a
Draft ASN (ownerEntityType='Asn' — LOCKED once Submitted), or a deferred-upload staging slot
(ownerEntityType='Staging' + a client-generated draft GUID). 5 MB cap. canWrite-gated against the supplier's
seccode (403 on mismatch). Returns DocumentAttachmentDto. Requires **Supplier.Write**.")]
    public async Task<Result<DocumentAttachmentDto>> Attach(
        [FromForm] IFormFile file,
        [FromForm] string ownerEntityType,
        [FromForm] Guid ownerEntityId,
        [FromForm] Guid supplierId,
        [FromServices] ICurrentUser user,
        [FromServices] SupplierWriteGuard guard,
        [FromServices] IDocumentValidationService docValidator,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Result<DocumentAttachmentDto>.Fail("File is required.");
        if (file.Length > MaxBytes)
            return Result<DocumentAttachmentDto>.Fail($"File exceeds {MaxBytes / 1024 / 1024} MB.");

        var isStaging = string.Equals(ownerEntityType, DocumentOwnerTypes.Staging, StringComparison.OrdinalIgnoreCase);
        var isLicense = string.Equals(ownerEntityType, DocumentOwnerTypes.SupplierLicense, StringComparison.OrdinalIgnoreCase);
        var isAsn = string.Equals(ownerEntityType, DocumentOwnerTypes.Asn, StringComparison.OrdinalIgnoreCase);
        if (!isStaging && !isLicense && !isAsn)
            return Result<DocumentAttachmentDto>.Fail($"ownerEntityType must be '{DocumentOwnerTypes.SupplierLicense}', '{DocumentOwnerTypes.Asn}' or '{DocumentOwnerTypes.Staging}'.");
        if (ownerEntityId == Guid.Empty)
            return Result<DocumentAttachmentDto>.Fail("ownerEntityId is required.");

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null)
            return Result<DocumentAttachmentDto>.Fail("Supplier not found.");

        // SecRight.canWrite gate against the supplier's G-seccode — throws ForbiddenException (403) on mismatch.
        await guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        // Direct attach: the license must exist and belong to this supplier (so the owner pointer is valid).
        if (isLicense)
        {
            var owns = await _db.SupplierLicenses
                .AnyAsync(l => l.Id == ownerEntityId && l.SupplierId == supplier.Id, ct);
            if (!owns)
                return Result<DocumentAttachmentDto>.Fail("License not found for this supplier.");
        }

        // Direct attach to an ASN: the ASN must belong to this supplier AND be Draft (lock-on-submit).
        if (isAsn)
        {
            var asn = await _db.Asns
                .Where(a => a.Id == ownerEntityId && a.SupplierId == supplier.Id)
                .Select(a => new { a.AsnStatus })
                .FirstOrDefaultAsync(ct);
            if (asn is null)
                return Result<DocumentAttachmentDto>.Fail("ASN not found for this supplier.");
            if (asn.AsnStatus != AsnStatus.Draft)
                return Result<DocumentAttachmentDto>.Fail("ASN is not Draft; attachments are locked once it is Submitted.");
        }

        var now = DateTime.UtcNow;
        var mime = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;

        await using var read = file.OpenReadStream();
        // Scope the storage path by the supplier's seccode (same shape the invite path uses with invite.Id).
        var stored = await _storage.StoreAsync(read, file.FileName, mime, supplier.SeccodeId, ct);
        var sizeKb = (int)Math.Ceiling(stored.SizeBytes / 1024d);

        var (resolvedOwnerType, resolvedDocType) =
            isLicense ? (DocumentOwnerTypes.SupplierLicense, DocumentType.License)
            : isAsn ? (DocumentOwnerTypes.Asn, DocumentType.AsnAttachment)
            : (DocumentOwnerTypes.Staging, DocumentType.License);   // Staging keeps License as a neutral default; rebind sets the real type.

        var docId = Guid.NewGuid();
        var doc = new DocumentUpload
        {
            Id = docId,
            OwnerEntityType = resolvedOwnerType,
            OwnerEntityId = ownerEntityId,
            DocumentType = resolvedDocType,
            FileName = file.FileName,
            FileUrl = stored.StorageKey,
            FileSizeKb = sizeKb,
            MimeType = mime,
            UploadedBy = user.UserCode,
            // Stamp the supplier's G-seccode so RLS picks the row up immediately AND so the rebinder can verify
            // ownership. Copy the supplier's tenant/company explicitly (deterministic — the row lands in the
            // supplier's company regardless of the request's active-company header; mirrors RegisterSupplierCommand's
            // doc rebind). The ScopeStampInterceptor only fills these when unset, so explicit values win.
            SeccodeId = supplier.SeccodeId,
            TenantId = supplier.TenantId,
            TenantEntityId = supplier.TenantEntityId,
            AiValidationStatus = AiValidationStatus.Pending,
            CreatedBy = user.UserCode,
            CreatedOn = now,
        };
        _db.DocumentUploads.Add(doc);
        await _db.SaveChangesAsync(ct);

        // Best-effort AI validation (mock today) — mirrors the registration flow's intent so the attachment
        // carries a validation status. Failures here must not fail the upload (the file is already persisted).
        try
        {
            var outcome = await docValidator.ValidateAsync(docId, ct);
            doc.AiValidationStatus = outcome.Status;
            doc.AiValidationConfidence = outcome.Confidence;
            doc.AiValidationPayload = outcome.Payload;
            doc.AiValidatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch { /* leave AiValidationStatus = Pending; status is advisory, not gating */ }

        return Result<DocumentAttachmentDto>.Ok(
            new DocumentAttachmentDto(docId, file.FileName, mime, stored.SizeBytes, now,
                $"files/proxy/{docId}", $"files/proxy/{docId}"),
            HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// Lists the attachments bound to a single supplier license (<c>ownerEntityType='SupplierLicense'</c>).
    /// Seccode-scoped by the always-on RLS filter, so callers only ever see their own supplier's rows.
    /// </summary>
    [HttpGet("by-license/{licenseId:guid}")]
    [Authorize(Policy = "Supplier.Write")]
    [EndpointSummary("List a license's attachments (authenticated)")]
    [EndpointDescription(@"Returns the documents attached to a SupplierLicense. Seccode-scoped (RLS). Returns
List<DocumentAttachmentDto> ordered by upload time. Requires **Supplier.Write**.")]
    public async Task<Result<List<DocumentAttachmentDto>>> ListByLicense(Guid licenseId, CancellationToken ct)
    {
        var items = await _db.DocumentUploads
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.SupplierLicense && d.OwnerEntityId == licenseId)
            .OrderBy(d => d.CreatedOn)
            .Select(d => new DocumentAttachmentDto(
                d.Id, d.FileName, d.MimeType, d.FileSizeKb * 1024L, d.CreatedOn,
                $"files/proxy/{d.Id}", $"files/proxy/{d.Id}"))
            .ToListAsync(ct);
        return Result<List<DocumentAttachmentDto>>.Ok(items, HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// R4 (2026-06-22) — Module 3. Lists the attachments bound to a single ASN (<c>ownerEntityType='Asn'</c>).
    /// Seccode-scoped by the always-on RLS filter, so callers only ever see their own supplier's rows. Read-only,
    /// so allowed regardless of the ASN's draft/submit state (the lock applies to attach/delete, not view).
    /// </summary>
    [HttpGet("by-asn/{asnId:guid}")]
    [Authorize(Policy = "Asn.Read")]
    [EndpointSummary("List an ASN's attachments (authenticated)")]
    [EndpointDescription(@"Returns the documents attached to an ASN (ownerEntityType='Asn'). Seccode-scoped (RLS).
Returns List<DocumentAttachmentDto> ordered by upload time. Requires **Asn.Read**.")]
    public async Task<Result<List<DocumentAttachmentDto>>> ListByAsn(Guid asnId, CancellationToken ct)
    {
        var items = await _db.DocumentUploads
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Asn && d.OwnerEntityId == asnId)
            .OrderBy(d => d.CreatedOn)
            .Select(d => new DocumentAttachmentDto(
                d.Id, d.FileName, d.MimeType, d.FileSizeKb * 1024L, d.CreatedOn,
                $"files/proxy/{d.Id}", $"files/proxy/{d.Id}"))
            .ToListAsync(ct);
        return Result<List<DocumentAttachmentDto>>.Ok(items, HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// Soft-deletes an attachment (the AuditableEntityInterceptor flips IsDeleted). canWrite-gated against the
    /// owning supplier's seccode. Works for <c>SupplierLicense</c>-, <c>Asn</c>- and <c>Staging</c>-owned rows
    /// (a user discarding a draft upload). ASN-owned attachments are LOCKED once the ASN is Submitted.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Supplier.Write")]
    [EndpointSummary("Delete a document attachment (authenticated, soft-delete)")]
    [EndpointDescription(@"Soft-deletes a DocumentUpload (SupplierLicense, Asn or Staging owned). canWrite-gated
against the owning supplier's seccode (403). ASN attachments are locked once the ASN is Submitted. Returns empty
success; 404 if not found; 409 if the owning ASN is locked. Requires **Supplier.Write**.")]
    public async Task<Result> Delete(
        Guid id,
        [FromServices] SupplierWriteGuard guard,
        CancellationToken ct)
    {
        var doc = await _db.DocumentUploads.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null)
            return Result.Fail("Attachment not found.");
        if (doc.OwnerEntityType != DocumentOwnerTypes.SupplierLicense
            && doc.OwnerEntityType != DocumentOwnerTypes.Asn
            && doc.OwnerEntityType != DocumentOwnerTypes.Staging)
            return Result.Fail("Attachment is not a deletable supplier/ASN attachment.");

        // Resolve the supplier that owns this attachment so we can canWrite-gate. SupplierLicense rows resolve
        // via the license -> supplier; Asn rows via the ASN -> supplier; Staging rows carry the supplier's
        // seccode directly (set at upload).
        Guid supplierId, seccodeId;
        if (doc.OwnerEntityType == DocumentOwnerTypes.SupplierLicense)
        {
            var lic = await _db.SupplierLicenses.FirstOrDefaultAsync(l => l.Id == doc.OwnerEntityId, ct);
            if (lic is null) return Result.Fail("Owning license not found.");
            supplierId = lic.SupplierId;
            seccodeId = lic.SeccodeId;
        }
        else if (doc.OwnerEntityType == DocumentOwnerTypes.Asn)
        {
            var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == doc.OwnerEntityId, ct);
            if (asn is null) return Result.Fail("Owning ASN not found.");
            if (asn.AsnStatus != AsnStatus.Draft)
                return Result.Fail("ASN is not Draft; attachments are locked once it is Submitted.");
            supplierId = asn.SupplierId;
            seccodeId = asn.SeccodeId;
        }
        else
        {
            // Staging: the seccode on the doc IS the supplier's G-seccode (stamped at upload). Map back to supplier.
            var sup = await _db.Suppliers.FirstOrDefaultAsync(s => s.SeccodeId == doc.SeccodeId, ct);
            if (sup is null) return Result.Fail("Owning supplier not found.");
            supplierId = sup.Id;
            seccodeId = sup.SeccodeId;
        }

        await guard.EnsureCanWriteAsync(supplierId, seccodeId, ct);

        _db.DocumentUploads.Remove(doc); // soft-delete: interceptor flips IsDeleted
        await _db.SaveChangesAsync(ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// Anonymous download — only succeeds when <paramref name="token"/> matches the invite that
    /// originally owned the doc (PendingInvite phase) OR matches the consumed invite still on
    /// record (post-registration browsing from the same invite link). Used by Register.razor to
    /// thumbnail freshly-uploaded files before login.
    /// </summary>
    [HttpGet("{id:guid}/by-token/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadByToken(Guid id, string token, CancellationToken ct)
    {
        // IgnoreQueryFilters — anonymous caller has no seccode rights, so the global seccode
        // query filter would hide PendingInvite-phase docs. The token check below provides the
        // authorization gate instead.
        var doc = await _db.DocumentUploads.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();

        var invite = await _db.SupplierInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Token == token, ct);
        if (invite is null) return NotFound();
        var ownsByInvite = doc.OwnerEntityType == PendingInviteOwnerType && doc.OwnerEntityId == invite.Id;
        var ownsBySupplier = doc.OwnerEntityType == "Supplier" && invite.SupplierId == doc.OwnerEntityId;
        if (!ownsByInvite && !ownsBySupplier) return NotFound();

        var stream = await _storage.OpenReadAsync(doc.FileUrl, ct);
        if (stream is null) return NotFound();
        // Force inline so browsers render PDFs/images directly inside <embed>/<img>. The
        // /files/proxy/{id} Web route forwards Content-Disposition; an "attachment" header
        // would trigger a download instead of inline preview. The DocumentPreviewer's
        // <a download="..."> attribute handles the download flow client-side.
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{System.Net.WebUtility.UrlEncode(doc.FileName)}\"";
        return File(stream, doc.MimeType);
    }

    /// <summary>Authenticated download — internal users browsing supplier-owned documents.</summary>
    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var doc = await _db.DocumentUploads.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        var stream = await _storage.OpenReadAsync(doc.FileUrl, ct);
        if (stream is null) return NotFound();
        // Force inline so browsers render PDFs/images directly inside <embed>/<img>. The
        // /files/proxy/{id} Web route forwards Content-Disposition; an "attachment" header
        // would trigger a download instead of inline preview. The DocumentPreviewer's
        // <a download="..."> attribute handles the download flow client-side.
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{System.Net.WebUtility.UrlEncode(doc.FileName)}\"";
        return File(stream, doc.MimeType);
    }
}
