namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3 / UC-ATT-01..07, Component 5 (Attachment Requirement Governance).
/// The result of evaluating the active attachment policy for a single transaction instance (ASN / Invoice /
/// Supplier) against the distinct attachment types already uploaded for it.
///
/// <para><see cref="MissingMandatory"/> = active <c>Mandatory</c> requirements with no matching upload — the
/// submit is BLOCKED and the message names these (UC-ATT-02). <see cref="MissingWarning"/> = active
/// <c>Warning</c> requirements with no matching upload — the submit is confirm-to-proceed (UC-ATT-03). Both lists
/// carry the attachment-type <b>display names</b> (e.g. "Test Certificate") for the user-facing message.</para>
///
/// <para>Mandatory is evaluated/surfaced BEFORE Warning (UC-ATT-05): when both are non-empty the caller blocks on
/// mandatory and the warning list is never shown until the mandatory items are uploaded. Absence of any policy
/// row → both lists empty → never blocks (UC-ATT-06).</para>
/// </summary>
public sealed record AttachmentEvaluation(
    IReadOnlyList<string> MissingMandatory,
    IReadOnlyList<string> MissingWarning)
{
    public static readonly AttachmentEvaluation None =
        new(Array.Empty<string>(), Array.Empty<string>());

    public bool HasMissingMandatory => MissingMandatory.Count > 0;
    public bool HasMissingWarning => MissingWarning.Count > 0;
}
