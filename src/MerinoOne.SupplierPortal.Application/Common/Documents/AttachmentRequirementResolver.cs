using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3 + decision D5, Component 5 (Attachment Requirement Governance). The PURE,
/// DB-less two-tier resolver. Given the flat set of active policy rows for one (tenant, entity) — a mix of tenant
/// defaults (<c>SupplierId == null</c>) and supplier overrides (<c>SupplierId == thisSupplier</c>) — plus the set
/// of attachment-type codes already present on the instance, it produces the effective per-type requirement and
/// splits out the missing Mandatory / Warning type names.
///
/// <para><b>Two-tier (D5), supplier WINS:</b> for each distinct (entity, type), the effective requirement is the
/// supplier-override row's requirement if a supplier-override row exists, else the tenant-default row's
/// requirement, else Optional. Types with no row at all are Optional and ignored.</para>
///
/// <para><b>Mandatory before Warning (UC-ATT-05):</b> the two lists are returned independently; the consumer
/// surfaces Mandatory first. <b>No policy rows → both lists empty (UC-ATT-06).</b></para>
///
/// Extracted as a static so it can be unit-tested with no database (the integration tests exercise the full
/// <see cref="AttachmentEvaluation"/> path on real SQL).
/// </summary>
public static class AttachmentRequirementResolver
{
    /// <summary>One active policy row, reduced to the fields resolution needs.</summary>
    /// <param name="TypeCode">The attachment-type code (aligns with DocumentUpload.documentType).</param>
    /// <param name="TypeName">The attachment-type display name (for the user-facing message).</param>
    /// <param name="SupplierId">NULL = tenant default; non-NULL = supplier override.</param>
    /// <param name="Requirement">Mandatory / Warning / Optional.</param>
    public readonly record struct PolicyRow(
        string TypeCode, string TypeName, Guid? SupplierId, AttachmentRequirement Requirement);

    /// <summary>
    /// Resolves the effective requirement per (entity, type) — supplier override wins over tenant default — then
    /// splits the missing-and-required types into Mandatory / Warning by display name.
    /// </summary>
    /// <param name="policies">Active policy rows for the (tenant, entity): tenant defaults + this supplier's overrides.</param>
    /// <param name="presentTypeCodes">Distinct attachment-type codes already uploaded for the instance (case-insensitive).</param>
    public static AttachmentEvaluation Resolve(
        IEnumerable<PolicyRow> policies,
        IEnumerable<string> presentTypeCodes)
    {
        var present = new HashSet<string>(
            presentTypeCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // Collapse to one effective row per type code: supplier override beats tenant default. Group by the
        // type code (case-insensitive) so a supplier-override row and a tenant-default row for the same type
        // resolve to a single effective requirement (supplier wins).
        var byType = policies
            .GroupBy(p => p.TypeCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var supplierRow = g.FirstOrDefault(p => p.SupplierId.HasValue);
                var effective = supplierRow.TypeCode is not null
                    ? supplierRow                                   // supplier override wins
                    : g.First(p => !p.SupplierId.HasValue);         // else the tenant default
                return effective;
            })
            .ToList();

        var missingMandatory = new List<string>();
        var missingWarning = new List<string>();

        foreach (var row in byType)
        {
            if (present.Contains(row.TypeCode)) continue;   // satisfied
            switch (row.Requirement)
            {
                case AttachmentRequirement.Mandatory:
                    missingMandatory.Add(row.TypeName);
                    break;
                case AttachmentRequirement.Warning:
                    missingWarning.Add(row.TypeName);
                    break;
                // Optional → silent (UC-ATT-04); never added.
            }
        }

        if (missingMandatory.Count == 0 && missingWarning.Count == 0)
            return AttachmentEvaluation.None;

        return new AttachmentEvaluation(
            missingMandatory.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            missingWarning.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
