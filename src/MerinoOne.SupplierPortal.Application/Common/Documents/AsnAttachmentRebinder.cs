using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// R4 (2026-06-22) — Module 3. Re-points deferred-upload <c>doc.DocumentUpload</c> rows from a transient staging
/// owner (<c>ownerEntityType='Staging'</c>, <c>ownerEntityId=&lt;clientDraftGuid&gt;</c>) onto a just-saved
/// <see cref="Domain.Entities.Proc.Asn"/> (<c>ownerEntityType='Asn'</c>, <c>DocumentType=AsnAttachment</c>).
/// Generalises <see cref="LicenseAttachmentRebinder"/> for the ASN owner type. Marks the rows Modified in the
/// supplied change tracker but does NOT call SaveChanges — the caller (Create/Update ASN command) saves once so
/// the ASN write AND the rebind commit (or roll back) atomically.
/// </summary>
public sealed class AsnAttachmentRebinder
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public AsnAttachmentRebinder(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    /// <summary>
    /// Rebinds staged attachments for <paramref name="stagingKey"/> onto the ASN. Only rows already carrying the
    /// ASN's <paramref name="asnSeccodeId"/> are rebound (the authenticated upload endpoint stamps that seccode at
    /// upload time, so cross-supplier staging keys can never leak in). When <paramref name="attachmentIds"/> is
    /// non-null it further restricts to that allow-list. No-op when <paramref name="stagingKey"/> is null/empty.
    /// </summary>
    public async Task RebindAsync(
        Guid? stagingKey,
        IReadOnlyList<Guid>? attachmentIds,
        Guid asnId,
        Guid asnSeccodeId,
        DateTime now,
        CancellationToken ct)
    {
        if (!stagingKey.HasValue || stagingKey.Value == Guid.Empty) return;

        var q = _db.DocumentUploads
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Staging
                        && d.OwnerEntityId == stagingKey.Value
                        && d.SeccodeId == asnSeccodeId
                        && !d.IsDeleted);

        if (attachmentIds is { Count: > 0 })
        {
            var allow = attachmentIds.ToList();
            q = q.Where(d => allow.Contains(d.Id));
        }

        var staged = await q.ToListAsync(ct);
        foreach (var doc in staged)
        {
            doc.OwnerEntityType = DocumentOwnerTypes.Asn;
            doc.OwnerEntityId = asnId;
            doc.DocumentType = nameof(DocumentType.AsnAttachment);
            doc.UpdatedBy = _user.UserCode;
            doc.UpdatedOn = now;
        }
    }
}
