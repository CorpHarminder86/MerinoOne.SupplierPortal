using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3 + decision D5, Component 5 (Attachment Requirement Governance). The
/// DB-backed two-tier evaluator. Loads the active <c>AttachmentRequirementPolicy</c> rows for the tenant + the
/// <c>AttachmentEntity</c> whose code == entityCode (joined to an ACTIVE <c>AttachmentType</c>), keeps only the
/// tenant defaults plus this supplier's overrides, then defers the supplier-wins resolution + missing split to the
/// pure <see cref="AttachmentRequirementResolver"/>.
///
/// <para><b>RLS note:</b> the policy / type / entity masters are owned by the tenant-admin seccode, so a supplier
/// (or buyer) principal holds no SecRight on them and the always-on seccode filter would hide every row — making
/// the gate silently never fire. The policy is tenant-WIDE configuration (not supplier-owned data), so this reads
/// it with <c>IgnoreQueryFilters()</c> scoped fail-closed by the caller's <c>TenantId</c> (the tenant guard is
/// re-applied by hand). Present-attachment detection reads <c>DocumentUpload</c> normally — those rows ARE
/// seccode/tenant-scoped to the instance the caller owns, which is exactly what we want to count.</para>
/// </summary>
public sealed class AttachmentPolicyEvaluator : IAttachmentPolicyEvaluator
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public AttachmentPolicyEvaluator(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<AttachmentEvaluation> EvaluateAsync(
        string entityCode, Guid entityId, Guid? supplierId, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        // Resolve the AttachmentEntity for this code (active). Tenant-scoped, filter-bypassed (config master).
        var entity = await _db.AttachmentEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.IsActive && e.Code == entityCode
                        && (tenantId == null || e.TenantId == tenantId))
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);

        // Unknown/inactive entity → nothing configured → never blocks (UC-ATT-06).
        if (entity is null) return AttachmentEvaluation.None;

        // Active policy rows for (tenant, entity), joined to ACTIVE types. Keep tenant defaults (supplierId NULL)
        // + THIS supplier's overrides only — other suppliers' overrides are irrelevant to this instance.
        var rows = await (
            from p in _db.AttachmentRequirementPolicies.IgnoreQueryFilters()
            join t in _db.AttachmentTypes.IgnoreQueryFilters() on p.AttachmentTypeId equals t.Id
            where !p.IsDeleted && p.IsActive
                  && p.AttachmentEntityId == entity.Id
                  && !t.IsDeleted && t.IsActive
                  && (tenantId == null || p.TenantId == tenantId)
                  && (p.SupplierId == null || p.SupplierId == supplierId)
            select new
            {
                t.Code,
                t.Name,
                p.SupplierId,
                p.Requirement,
            }).ToListAsync(ct);

        // No policy rows at all → Optional everywhere → never blocks (UC-ATT-06).
        if (rows.Count == 0) return AttachmentEvaluation.None;

        // Present types = distinct DocumentUpload.documentType codes for (ownerEntityType=entityCode,
        // ownerEntityId=entityId), not soft-deleted. DocumentType is a string code that aligns with
        // AttachmentType.Code; the resolver compares case-insensitively.
        var presentCodes = await _db.DocumentUploads
            .Where(d => d.OwnerEntityType == entityCode && d.OwnerEntityId == entityId)
            .Select(d => d.DocumentType)
            .Distinct()
            .ToListAsync(ct);

        var policyRows = rows.Select(r =>
            new AttachmentRequirementResolver.PolicyRow(r.Code, r.Name, r.SupplierId, r.Requirement));

        return AttachmentRequirementResolver.Resolve(policyRows, presentCodes);
    }
}
