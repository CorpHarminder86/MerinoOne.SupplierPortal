using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Documents;

/// <summary>
/// R19 — auto-attaches a portal-generated report PDF (Packing List on an ASN, Supplier Invoice on an Invoice) to the
/// owner's attachment panel the moment a SUPPLIER downloads it.
///
/// <para><b>Rules.</b> (1) Supplier users only — an internal / buyer / admin download never attaches. (2) Only when the
/// panel would actually render the slot: the tenant's ACTIVE <c>AttachmentType</c> for the report type, the ACTIVE
/// <c>AttachmentEntity</c> for the owner type, and at least one ACTIVE policy for (entity, type) that applies to the
/// owner's supplier (tenant default or that supplier's override) — the same slot <c>GetAttachmentPanelQuery</c> builds. (3)
/// REPLACE: every earlier SYSTEM-GENERATED document of the same (owner, type) is soft-deleted and the new one inserted
/// in one SaveChanges; a user's manual upload of the same type is never touched. (4) The owner's lock state (ASN
/// posted, invoice submitted) is deliberately ignored — the generated report is not a user edit.</para>
///
/// <para>The caller (an API endpoint) has already authorized read access to the owner; this class re-checks only the
/// supplier-identity and enablement rules. Storage is written BEFORE the database is touched, so a storage failure
/// (which propagates) leaves no half state.</para>
/// </summary>
public sealed class GeneratedReportAttacher
{
    private const string PdfMime = "application/pdf";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IFileStorageService _storage;

    public GeneratedReportAttacher(IAppDbContext db, ICurrentUser user, IFileStorageService storage)
    {
        _db = db;
        _user = user;
        _storage = storage;
    }

    /// <summary>True when attached; false when not enabled or the caller is not a supplier user. Never throws for those cases.</summary>
    public async Task<bool> AttachAsync(
        string ownerEntityType, Guid ownerEntityId, string documentType,
        string fileName, byte[] pdf, CancellationToken ct)
    {
        if (ownerEntityId == Guid.Empty || string.IsNullOrWhiteSpace(documentType)
            || string.IsNullOrWhiteSpace(fileName) || pdf is null || pdf.Length == 0)
            return false;

        // Supplier-only — cheapest check first (no query): internal roles never attach.
        if (!_user.IsAuthenticated || _user.IsAdmin || _user.IsManager || _user.IsPlatformAdmin
            || !_user.Roles.Contains(RoleNames.Supplier))
            return false;

        // 1. Resolve the owner (filter-bypassed; the caller already authorized read access) → its supplier.
        Guid? ownerSupplierId;
        string canonicalOwnerType;
        if (string.Equals(ownerEntityType, DocumentOwnerTypes.Asn, StringComparison.OrdinalIgnoreCase))
        {
            canonicalOwnerType = DocumentOwnerTypes.Asn;
            ownerSupplierId = await _db.Asns.IgnoreQueryFilters()
                .Where(a => a.Id == ownerEntityId && !a.IsDeleted)
                .Select(a => (Guid?)a.SupplierId).FirstOrDefaultAsync(ct);
        }
        else if (string.Equals(ownerEntityType, DocumentOwnerTypes.Invoice, StringComparison.OrdinalIgnoreCase))
        {
            canonicalOwnerType = DocumentOwnerTypes.Invoice;
            ownerSupplierId = await _db.Invoices.IgnoreQueryFilters()
                .Where(i => i.Id == ownerEntityId && !i.IsDeleted)
                .Select(i => (Guid?)i.SupplierId).FirstOrDefaultAsync(ct);
        }
        else
        {
            return false;
        }
        if (ownerSupplierId is null) return false;

        var supplier = await _db.Suppliers.IgnoreQueryFilters()
            .Where(s => s.Id == ownerSupplierId.Value && !s.IsDeleted)
            .Select(s => new { s.Id, s.SeccodeId, s.TenantId, s.TenantEntityId })
            .FirstOrDefaultAsync(ct);
        if (supplier is null || supplier.TenantId is null) return false;
        var tenantId = supplier.TenantId;

        // 2. The caller must be a user OF THIS owner's supplier: a SecRight on the supplier's G-seccode (what
        //    SupplierUserMap grants — the same membership that scopes the supplier's rows). Role alone is not enough:
        //    a supplier user must not attach onto another supplier's owner.
        var userCode = _user.UserCode;
        var isSupplierMember = await _db.SecRights.IgnoreQueryFilters()
            .AnyAsync(r => r.SeccodeId == supplier.SeccodeId && r.UserCode == userCode && r.CanRead && !r.IsDeleted, ct);
        if (!isSupplierMember) return false;

        // 3. Enabled — the slot the attachment panel would render for (tenant, owner type, report type, supplier).
        var entityId = await _db.AttachmentEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.IsActive && e.Code == canonicalOwnerType && e.TenantId == tenantId)
            .Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
        if (entityId is null) return false;

        var typeId = await _db.AttachmentTypes.IgnoreQueryFilters()
            .Where(t => !t.IsDeleted && t.IsActive && t.Code == documentType && t.TenantId == tenantId)
            .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (typeId is null) return false;

        // D5: the panel keeps only ACTIVE rows (tenant default + this supplier's override), so "any active row" is
        // exactly "the slot exists" — an inactive override with an active default still yields the slot.
        var slotExists = await _db.AttachmentRequirementPolicies.IgnoreQueryFilters()
            .AnyAsync(p => !p.IsDeleted && p.IsActive
                           && p.TenantId == tenantId
                           && p.AttachmentEntityId == entityId.Value
                           && p.AttachmentTypeId == typeId.Value
                           && (p.SupplierId == null || p.SupplierId == supplier.Id), ct);
        if (!slotExists) return false;

        // 4. STORE first — if storage throws nothing in the database has changed.
        await using var content = new MemoryStream(pdf, writable: false);
        var stored = await _storage.StoreAsync(content, fileName, PdfMime, supplier.SeccodeId, ct);

        // 5. REPLACE — soft-delete the earlier system-generated docs of this (owner, type); manual uploads untouched.
        var previous = await _db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => d.OwnerEntityType == canonicalOwnerType && d.OwnerEntityId == ownerEntityId
                        && !d.IsDeleted && d.IsSystemGenerated)
            .ToListAsync(ct);
        foreach (var old in previous.Where(d => string.Equals(d.DocumentType, documentType, StringComparison.OrdinalIgnoreCase)))
            _db.DocumentUploads.Remove(old);   // AuditableEntityInterceptor converts to a soft delete

        var now = DateTime.UtcNow;
        _db.DocumentUploads.Add(new DocumentUpload
        {
            Id = Guid.NewGuid(),
            OwnerEntityType = canonicalOwnerType,
            OwnerEntityId = ownerEntityId,
            DocumentType = documentType,
            FileName = fileName,
            FileUrl = stored.StorageKey,
            FileSizeKb = (long)Math.Ceiling(stored.SizeBytes / 1024d),
            MimeType = PdfMime,
            UploadedBy = userCode,
            SeccodeId = supplier.SeccodeId,
            TenantId = supplier.TenantId,
            TenantEntityId = supplier.TenantEntityId,
            AiValidationStatus = AiValidationStatus.Pending,   // no AI call for a portal-generated report
            IsSystemGenerated = true,
            CreatedBy = userCode,
            CreatedOn = now,
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
