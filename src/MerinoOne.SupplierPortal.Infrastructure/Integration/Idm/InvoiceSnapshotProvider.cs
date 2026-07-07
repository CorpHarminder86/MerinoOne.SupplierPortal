using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.2. Assembles the Invoice→IDM snapshot (the only per-entity code). Runs with
/// IgnoreQueryFilters + an explicit tenant predicate (background scope). base64 is injected here (D-R8-18), only
/// when requested. NOTE: the portal has no separate LN "logistic company" field, so <c>logisticCompany</c> mirrors
/// <c>financialCompany</c> (= erpCompany) pending Live confirmation (see infor-live-cutover-checklist).
/// </summary>
public sealed class InvoiceSnapshotProvider : IEntitySnapshotProvider
{
    private readonly IAppDbContext _db;
    private readonly IFileContentProvider _files;

    public InvoiceSnapshotProvider(IAppDbContext db, IFileContentProvider files)
    {
        _db = db;
        _files = files;
    }

    public string IdmEntityType => "InforInvoice";
    public string OwnerEntityType => DocumentOwnerTypes.Invoice;

    public async Task<object?> BuildSnapshotAsync(Guid tenantId, Guid ownerEntityId, Guid documentUploadId, bool includeFileContent, CancellationToken ct)
    {
        var inv = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ownerEntityId && x.TenantId == tenantId && !x.IsDeleted, ct);
        var doc = await _db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentUploadId && x.TenantId == tenantId, ct);
        if (inv is null || doc is null) return null;

        var base64 = includeFileContent ? await _files.ToBase64Async(doc.FileUrl, ct) : null;
        var (acl, entityName) = await IdmConfigDefaults.ResolveAsync(_db, tenantId, IdmEntityType, ct);

        return new Dictionary<string, object?>
        {
            ["entityType"] = IdmEntityType,
            ["invoice"] = new Dictionary<string, object?>
            {
                ["financialCompany"] = inv.ErpCompany,
                ["logisticCompany"] = inv.ErpCompany,
                ["transactionType"] = inv.ErpTransactionType,
                ["lnInvoiceNumber"] = inv.ErpDocumentNo,
                ["erpCompany"] = inv.ErpCompany,
                ["erpTransactionType"] = inv.ErpTransactionType,
                ["erpDocumentNo"] = inv.ErpDocumentNo,
            },
            ["attachment"] = new Dictionary<string, object?>
            {
                ["filename"] = doc.FileName,
                ["base64"] = base64,
            },
            ["config"] = new Dictionary<string, object?>
            {
                ["acl"] = acl,
                ["entityName"] = entityName,
            },
            ["pid"] = doc.Pid ?? string.Empty,
        };
    }
}
