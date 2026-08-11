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

        // R11.2 (2026-07-29) — the ack-stamped ErpCompany/ErpTransactionType/ErpDocumentNo columns are gone
        // from the ASN. The company is the code already held via TenantEntityId (user-confirmed identical to
        // what LN acks as erpCompany); both company slots carry it. transactionType/lnDocumentNumber have no
        // source any more — the mapping that consumed them was wrong and is being replaced (config Held until
        // the fresh mapping lands), so the keys are gone from the snapshot contract.
        var companyCode = asn.TenantEntityId is { } companyId
            ? await _db.TenantEntities.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == companyId && !t.IsDeleted)
                .Select(t => t.Code).FirstOrDefaultAsync(ct)
            : null;

        // R16.1 (2026-08-11) — the fresh mapping's MDS_id1 is the SUPPLIER's ERP code (BUS…), so the snapshot
        // grows an asn.supplier block. It is ALSO a gate term: a supplier with no erpCode holds the document
        // Blocked rather than pushing an item IDM cannot key.
        var supplier = await _db.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == asn.SupplierId && !s.IsDeleted)
            .Select(s => new { s.ErpCode, s.SupplierCode })
            .FirstOrDefaultAsync(ct);

        var base64 = includeFileContent ? await _files.ToBase64Async(doc.FileUrl, ct) : null;

        return new Dictionary<string, object?>
        {
            ["entityType"] = IdmEntityType,
            ["asn"] = new Dictionary<string, object?>
            {
                ["financialCompany"] = companyCode,
                ["logisticCompany"] = companyCode,
                ["companyCode"] = companyCode,
                // R10 (2026-07-07) — portal identifiers the mapping/gate expressions kept reaching for.
                ["asnNumber"] = asn.AsnNumber,
                ["erpCode"] = asn.ErpCode,
                ["erpSyncId"] = asn.ErpSyncId,
                ["status"] = asn.AsnStatus.ToString(),
                ["supplier"] = new Dictionary<string, object?>
                {
                    ["erpCode"] = supplier?.ErpCode,
                    ["supplierCode"] = supplier?.SupplierCode,
                },
            },
            ["attachment"] = new Dictionary<string, object?>
            {
                ["filename"] = doc.FileName,
                ["base64"] = base64,
            },
            ["pid"] = doc.Pid ?? string.Empty,
        };
    }
}
