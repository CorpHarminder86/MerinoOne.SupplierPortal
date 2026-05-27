using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
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
