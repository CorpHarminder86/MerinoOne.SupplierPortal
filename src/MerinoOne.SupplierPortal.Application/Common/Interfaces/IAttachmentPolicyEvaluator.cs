using MerinoOne.SupplierPortal.Application.Common.Documents;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3 + decision D5, Component 5 (Attachment Requirement Governance).
/// Evaluates the active attachment-requirement policy for a single transaction instance at submit time and
/// returns the missing Mandatory / Warning attachment types (by display name).
///
/// <para>Two-tier (D5): the tenant default (<c>supplierId IS NULL</c>) is overridden per type by the supplier's
/// own override row (<c>supplierId == this supplier</c>) — supplier WINS. Types with no row → Optional (ignored).
/// No policy rows at all → empty evaluation (never blocks — UC-ATT-06). See
/// <see cref="AttachmentRequirementResolver"/> for the pure resolution rule.</para>
/// </summary>
public interface IAttachmentPolicyEvaluator
{
    /// <param name="entityCode">The <c>doc.AttachmentEntity.code</c> ("Supplier" | "Asn" | "Invoice"); aligns
    /// with <c>DocumentUpload.OwnerEntityType</c>.</param>
    /// <param name="entityId">The instance id (matches <c>DocumentUpload.OwnerEntityId</c>).</param>
    /// <param name="supplierId">The instance's owning supplier, for the supplier-override tier. Null → only the
    /// tenant default applies.</param>
    Task<AttachmentEvaluation> EvaluateAsync(
        string entityCode, Guid entityId, Guid? supplierId, CancellationToken ct);
}
