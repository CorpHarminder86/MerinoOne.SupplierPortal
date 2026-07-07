using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.2b / D-R8-25. Assembles the ASN→IDM snapshot. Same envelope shape as Invoice; only
/// the block name (<c>asn</c>), attr values and <c>entityType</c> differ. Runs with IgnoreQueryFilters + explicit
/// tenant predicate; base64 injected only when requested.
/// </summary>
public sealed class AsnSnapshotProvider : IEntitySnapshotProvider
{
    private readonly IAppDbContext _db;
    private readonly IFileContentProvider _files;

    public AsnSnapshotProvider(IAppDbContext db, IFileContentProvider files)
    {
        _db = db;
        _files = files;
    }

    public string IdmEntityType => "InforAdvanceShipmentNoticeSupplierASN";
    public string OwnerEntityType => DocumentOwnerTypes.Asn;

    public async Task<object?> BuildSnapshotAsync(Guid tenantId, Guid ownerEntityId, Guid documentUploadId, bool includeFileContent, CancellationToken ct)
    {
        var asn = await _db.Asns.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ownerEntityId && x.TenantId == tenantId && !x.IsDeleted, ct);
        var doc = await _db.DocumentUploads.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentUploadId && x.TenantId == tenantId, ct);
        if (asn is null || doc is null) return null;

        var base64 = includeFileContent ? await _files.ToBase64Async(doc.FileUrl, ct) : null;
        var (acl, entityName) = await IdmConfigDefaults.ResolveAsync(_db, tenantId, IdmEntityType, ct);

        return new Dictionary<string, object?>
        {
            ["entityType"] = IdmEntityType,
            ["asn"] = new Dictionary<string, object?>
            {
                ["financialCompany"] = asn.ErpCompany,
                ["logisticCompany"] = asn.ErpCompany,
                ["transactionType"] = asn.ErpTransactionType,
                ["lnDocumentNumber"] = asn.ErpDocumentNo,
                ["erpCompany"] = asn.ErpCompany,
                ["erpTransactionType"] = asn.ErpTransactionType,
                ["erpDocumentNo"] = asn.ErpDocumentNo,
                // R10 (2026-07-07) — portal identifiers the mapping/gate expressions kept reaching for.
                ["asnNumber"] = asn.AsnNumber,
                ["erpCode"] = asn.ErpCode,
                ["erpSyncId"] = asn.ErpSyncId,
                ["status"] = asn.AsnStatus.ToString(),
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
