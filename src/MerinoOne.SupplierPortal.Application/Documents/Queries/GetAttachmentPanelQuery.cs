using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Documents.Queries;

/// <summary>
/// R5 (TSD R5 Addendum §13.8) — Component 9: the Policy-Driven Attachment Panel READ-MODEL. Returns one
/// <see cref="AttachmentPanelSlotDto"/> per ACTIVE <c>AttachmentRequirementPolicy</c> type for the (tenant,
/// <paramref name="EntityCode"/>) — each carrying its effective two-tier (D5) requirement and ALL its uploaded
/// files (§13.4). An EMPTY list ⇒ no active policy ⇒ the host renders no panel (§13.2). Slots are ordered
/// Mandatory → Warning → Optional, then alphabetical by type name (§13.3).
///
/// <para><b>Batched, no N+1.</b> The handler runs essentially TWO queries: (1) the active policy rows joined to
/// active <see cref="Domain.Entities.Doc.AttachmentType"/> (the D5 tier-filtered set, mirroring
/// <see cref="AttachmentPolicyEvaluator"/>'s policy-load shape), and (2) ALL non-deleted <c>DocumentUpload</c>
/// rows for (ownerEntityType=EntityCode, ownerEntityId=EntityId) in one go. Documents are grouped by
/// <c>documentType</c> (case-insensitive) IN MEMORY and the effective per-type requirement is resolved via the
/// pure <see cref="AttachmentRequirementResolver"/>. There is NO per-slot query loop.</para>
///
/// <para><b>Purely descriptive — no enforcement.</b> This query never blocks, confirms, or audits. Enforcement
/// of Mandatory/Warning stays at each host's submit site (the R4 <c>AttachmentSubmitGuard</c>, wired for ASN at
/// Send-for-Approval, §13.6 / §10.3). Mutations (upload / remove / download) go through
/// <c>DocumentUploadsController</c> — this read-model only describes the slots.</para>
/// </summary>
/// <param name="EntityCode">The <c>AttachmentEntity.code</c> ("Asn" | "Invoice" | "Supplier"); aligns with
/// <c>DocumentUpload.OwnerEntityType</c>.</param>
/// <param name="EntityId">The instance id (matches <c>DocumentUpload.OwnerEntityId</c>).</param>
/// <param name="SupplierId">Optional override for the supplier-override (D5) tier. When null the handler resolves
/// the owning supplier from the entity (Supplier ⇒ the entityId itself; Asn/Invoice ⇒ the owning supplier).</param>
public record GetAttachmentPanelQuery(string EntityCode, Guid EntityId, Guid? SupplierId = null)
    : IRequest<IReadOnlyList<AttachmentPanelSlotDto>>;

public sealed class GetAttachmentPanelQueryHandler
    : IRequestHandler<GetAttachmentPanelQuery, IReadOnlyList<AttachmentPanelSlotDto>>
{
    private static readonly IReadOnlyList<AttachmentPanelSlotDto> Empty = Array.Empty<AttachmentPanelSlotDto>();

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GetAttachmentPanelQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<IReadOnlyList<AttachmentPanelSlotDto>> Handle(
        GetAttachmentPanelQuery request, CancellationToken ct)
    {
        var entityCode = request.EntityCode?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(entityCode) || request.EntityId == Guid.Empty)
            return Empty;

        var tenantId = _user.TenantId;

        // Resolve the AttachmentEntity for this code (active). Tenant-scoped, filter-bypassed (config master) —
        // mirrors AttachmentPolicyEvaluator. Unknown/inactive entity → no panel (§13.2).
        var entity = await _db.AttachmentEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && e.IsActive && e.Code == entityCode
                        && (tenantId == null || e.TenantId == tenantId))
            .Select(e => new { e.Id })
            .FirstOrDefaultAsync(ct);
        if (entity is null) return Empty;

        // Resolve the supplier for the D5 supplier-override tier (where applicable). Supplier ⇒ the entityId IS the
        // supplier; Asn/Invoice ⇒ the owning supplier; otherwise only the tenant default applies.
        var supplierId = await ResolveSupplierIdAsync(entityCode, request.EntityId, request.SupplierId, ct);

        // QUERY 1 — active policy rows for (tenant, entity) joined to ACTIVE AttachmentType, D5 tier-filtered
        // (tenant defaults [SupplierId == null] + THIS supplier's overrides only). Copies the evaluator's shape.
        var policyRows = await (
            from p in _db.AttachmentRequirementPolicies.IgnoreQueryFilters()
            join t in _db.AttachmentTypes.IgnoreQueryFilters() on p.AttachmentTypeId equals t.Id
            where !p.IsDeleted && p.IsActive
                  && p.AttachmentEntityId == entity.Id
                  && !t.IsDeleted && t.IsActive
                  && (tenantId == null || p.TenantId == tenantId)
                  && (p.SupplierId == null || p.SupplierId == supplierId)
            select new { t.Code, t.Name, p.SupplierId, p.Requirement }).ToListAsync(ct);

        // No active policy rows → empty list → host renders no panel (the "no policy → no control" rule, §13.2).
        if (policyRows.Count == 0) return Empty;

        // QUERY 2 — ALL non-deleted DocumentUpload rows for (ownerEntityType=entityCode, ownerEntityId=entityId),
        // in ONE query. RLS-scoped (the supplier's own docs); the supplier-detail / ASN-detail read paths read
        // DocumentUpload the same way.
        var docs = await _db.DocumentUploads
            .Where(d => d.OwnerEntityType == entityCode && d.OwnerEntityId == request.EntityId && !d.IsDeleted)
            .OrderBy(d => d.CreatedOn)
            .Select(d => new
            {
                d.Id, d.DocumentType, d.FileName, d.UploadedBy, d.CreatedOn, d.MimeType, d.FileSizeKb,
            })
            .ToListAsync(ct);

        // Group the documents by documentType (case-insensitive) IN MEMORY — no per-slot query.
        var docsByType = docs
            .GroupBy(d => d.DocumentType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Collapse the D5 two-tier policy rows to ONE effective requirement per type (supplier override wins) via
        // the pure resolver, so the panel badge matches the enforcement evaluator exactly.
        var resolverRows = policyRows.Select(r =>
            new AttachmentRequirementResolver.PolicyRow(r.Code, r.Name, r.SupplierId, r.Requirement));
        var effectiveByType = AttachmentRequirementResolver.ResolveEffective(resolverRows);

        var slots = effectiveByType.Select(row =>
        {
            var files = docsByType.TryGetValue(row.TypeCode, out var list)
                ? list.Select(d => new AttachmentPanelDocumentDto(
                        d.Id, d.FileName, d.UploadedBy, d.CreatedOn,
                        $"files/proxy/{d.Id}", d.MimeType, d.FileSizeKb * 1024L))
                    .ToList()
                : new List<AttachmentPanelDocumentDto>();

            return new AttachmentPanelSlotDto(
                row.TypeCode, row.TypeName, row.Requirement.ToString(), files);
        });

        // §13.3 ordering: Mandatory → Warning → Optional, then alphabetical by type name.
        return slots
            .OrderBy(s => RequirementRank(s.Requirement))
            .ThenBy(s => s.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RequirementRank(string requirement) => requirement switch
    {
        nameof(AttachmentRequirement.Mandatory) => 0,
        nameof(AttachmentRequirement.Warning) => 1,
        _ => 2, // Optional (and any future level) last
    };

    /// <summary>
    /// Resolves the supplier for the D5 supplier-override tier. Explicit override wins; else Supplier ⇒ entityId,
    /// Asn/Invoice ⇒ the owning supplier; else null (only the tenant default applies). Filter-bypassed lookups so
    /// the resolution is deterministic regardless of the caller's active-company / seccode scope.
    /// </summary>
    private async Task<Guid?> ResolveSupplierIdAsync(
        string entityCode, Guid entityId, Guid? explicitSupplierId, CancellationToken ct)
    {
        if (explicitSupplierId.HasValue) return explicitSupplierId;

        if (string.Equals(entityCode, DocumentOwnerTypes.Supplier, StringComparison.OrdinalIgnoreCase))
            return entityId;

        if (string.Equals(entityCode, DocumentOwnerTypes.Asn, StringComparison.OrdinalIgnoreCase))
            return await _db.Asns.IgnoreQueryFilters()
                .Where(a => a.Id == entityId).Select(a => (Guid?)a.SupplierId).FirstOrDefaultAsync(ct);

        if (string.Equals(entityCode, DocumentOwnerTypes.Invoice, StringComparison.OrdinalIgnoreCase))
            return await _db.Invoices.IgnoreQueryFilters()
                .Where(i => i.Id == entityId).Select(i => (Guid?)i.SupplierId).FirstOrDefaultAsync(ct);

        return null;
    }
}
