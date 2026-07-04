using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Documents;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Documents.Queries;

/// <summary>
/// 2026-07-04 — the cross-module document register. A single paged, RLS-scoped list over every
/// <c>doc.DocumentUpload</c> row (attachments were previously only visible per owning entity). It runs WITHOUT
/// <c>IgnoreQueryFilters</c>, so the always-on seccode RLS (plus tenant/company/soft-delete) scopes each caller:
/// an admin sees the whole tenant, a supplier only their own documents. Owner handles (ASN no / invoice no /
/// supplier code / license no) are resolved in a second, also-RLS-filtered pass — never leaking an owner the
/// caller cannot see (falls back to the short owner id in the UI).
/// </summary>
public record GetDocumentsQuery(
    int Page = 1, int PageSize = 50, string? OwnerEntityType = null, string? DocumentType = null,
    string? FileName = null, DateTime? FromDate = null, DateTime? ToDate = null,
    string? IdmStatus = null, Guid? SupplierId = null) : IRequest<PagedResult<DocumentListItemDto>>;

public class GetDocumentsQueryHandler : IRequestHandler<GetDocumentsQuery, PagedResult<DocumentListItemDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetDocumentsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PagedResult<DocumentListItemDto>> Handle(GetDocumentsQuery request, CancellationToken ct)
    {
        // RLS (seccode) filters apply — NO IgnoreQueryFilters. Explicit tenant guard as defence-in-depth.
        var tid = _user.TenantId;
        var q = _db.DocumentUploads.Where(d => d.TenantId == tid);

        if (!string.IsNullOrWhiteSpace(request.OwnerEntityType)) q = q.Where(d => d.OwnerEntityType == request.OwnerEntityType);
        if (!string.IsNullOrWhiteSpace(request.DocumentType)) q = q.Where(d => d.DocumentType == request.DocumentType);
        if (!string.IsNullOrWhiteSpace(request.FileName)) q = q.Where(d => d.FileName.Contains(request.FileName));
        if (request.FromDate.HasValue) { var f = request.FromDate.Value.Date; q = q.Where(d => d.CreatedOn >= f); }
        if (request.ToDate.HasValue) { var t = request.ToDate.Value.Date.AddDays(1); q = q.Where(d => d.CreatedOn < t); }
        if (string.Equals(request.IdmStatus, "Synced", StringComparison.OrdinalIgnoreCase)) q = q.Where(d => d.Pid != null);
        else if (string.Equals(request.IdmStatus, "NotSynced", StringComparison.OrdinalIgnoreCase)) q = q.Where(d => d.Pid == null);

        if (request.SupplierId.HasValue)
        {
            var docIds = await DocumentOwnerSupplierResolver.ResolveDocumentIdsForSupplierAsync(_db, request.SupplierId.Value, ct);
            q = q.Where(d => docIds.Contains(d.Id));
        }

        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var total = await q.CountAsync(ct);

        var rows = await q.OrderByDescending(d => d.Seq)
            .Skip((request.Page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new Row(d.Id, d.Seq, d.FileName, d.DocumentType, d.OwnerEntityType, d.OwnerEntityId,
                d.FileSizeKb, d.MimeType, d.UploadedBy, d.CreatedOn, d.IdmEntityType, d.Pid))
            .ToListAsync(ct);

        var ownerRefs = await ResolveOwnerRefsAsync(rows, ct);
        var supplierDisplay = await DocumentOwnerSupplierResolver.ResolveSupplierDisplayAsync(
            _db, rows.Select(r => (r.OwnerEntityType, r.OwnerEntityId)), ct);

        var items = rows.Select(r =>
        {
            supplierDisplay.TryGetValue((r.OwnerEntityType, r.OwnerEntityId), out var sup);
            return new DocumentListItemDto(
                r.Id, r.Seq, r.FileName, r.DocumentType, r.OwnerEntityType, r.OwnerEntityId,
                ownerRefs.GetValueOrDefault(r.OwnerEntityId), r.FileSizeKb, r.MimeType, r.UploadedBy,
                r.CreatedOn, r.IdmEntityType, r.Pid, sup.Code, sup.Name);
        }).ToList();

        return new PagedResult<DocumentListItemDto> { Items = items, Page = request.Page, PageSize = pageSize, TotalCount = total };
    }

    // Batch-resolve each owner type's human ref. Each sub-query is ALSO RLS-filtered (no IgnoreQueryFilters), so an
    // owner the caller cannot see simply doesn't resolve (→ null → short-id fallback), never a cross-scope leak.
    private async Task<Dictionary<Guid, string>> ResolveOwnerRefsAsync(List<Row> rows, CancellationToken ct)
    {
        var refs = new Dictionary<Guid, string>();

        async Task AddAsync(string ownerType, Func<List<Guid>, Task<List<KeyValuePair<Guid, string>>>> fetch)
        {
            var ids = rows.Where(r => r.OwnerEntityType == ownerType).Select(r => r.OwnerEntityId).Distinct().ToList();
            if (ids.Count == 0) return;
            foreach (var kv in await fetch(ids))
                refs[kv.Key] = kv.Value;
        }

        await AddAsync(DocumentOwnerTypes.Asn, async ids => (await _db.Asns
            .Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, Ref = a.AsnNumber }).ToListAsync(ct))
            .Select(x => new KeyValuePair<Guid, string>(x.Id, x.Ref)).ToList());

        await AddAsync(DocumentOwnerTypes.Invoice, async ids => (await _db.Invoices
            .Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, Ref = i.InvoiceNumber }).ToListAsync(ct))
            .Select(x => new KeyValuePair<Guid, string>(x.Id, x.Ref)).ToList());

        await AddAsync(DocumentOwnerTypes.Supplier, async ids => (await _db.Suppliers
            .Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, Ref = s.SupplierCode }).ToListAsync(ct))
            .Select(x => new KeyValuePair<Guid, string>(x.Id, x.Ref)).ToList());

        await AddAsync(DocumentOwnerTypes.SupplierLicense, async ids => (await _db.SupplierLicenses
            .Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, Ref = l.LicenseNumber }).ToListAsync(ct))
            .Select(x => new KeyValuePair<Guid, string>(x.Id, x.Ref)).ToList());

        return refs;
    }

    private sealed record Row(Guid Id, int Seq, string FileName, string DocumentType, string OwnerEntityType,
        Guid OwnerEntityId, long FileSizeKb, string MimeType, string UploadedBy, DateTime CreatedOn,
        string? IdmEntityType, string? Pid);
}
