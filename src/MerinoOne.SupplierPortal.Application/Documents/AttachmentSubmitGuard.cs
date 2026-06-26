using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Documents;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §8.3 / §8.4 / UC-ATT-01..05, Component 5. The single enforcement point the
/// three submit handlers (ASN / Invoice / Supplier) call BEFORE their state transition. Encapsulates the
/// mandatory-block / warning-confirm / skip-audit policy so the three call sites stay one-liners and behave
/// identically:
/// <list type="number">
///   <item><b>Mandatory missing</b> → throw <see cref="ValidationException"/> naming the types (the hard block,
///         no proceed — UC-ATT-02 / UC-ATT-05). Mandatory is evaluated BEFORE Warning.</item>
///   <item><b>Warning missing AND not acknowledged</b> → return <see cref="AttachmentSubmitDecision"/> with
///         <c>RequiresConfirmation == true</c> + the warning type names. The caller must NOT proceed; it surfaces
///         the confirm prompt (UC-ATT-03). This is NOT an error (no exception).</item>
///   <item><b>Otherwise proceed.</b> If a warning was skipped because the caller acknowledged it, write a targeted
///         skip <see cref="AuditEntry"/> (who / when / entity / which type names) so the compliance-relevant skip
///         is on the trail (§8.4). The audit is staged on the context; it commits with the handler's own
///         <c>SaveChangesAsync</c> in the same transaction.</item>
/// </list>
/// </summary>
public sealed class AttachmentSubmitGuard
{
    // CK_AuditEntry_operation constrains operation to Insert/Update/Delete (no custom ops), so the skip row uses
    // "Update" and stays identifiable by its "Attachment warning skipped" FieldName — mirrors the established
    // PoConfirmationGateEnforcer / PoNegotiationHistory targeted-audit convention.
    private const string SkipOp = "Update";
    private const string SkipField = "Attachment warning skipped";
    private const int FieldMax = 100;   // audit.AuditEntry.fieldName is nvarchar(100).

    private readonly IAttachmentPolicyEvaluator _evaluator;
    private readonly ICurrentUser _user;

    public AttachmentSubmitGuard(IAttachmentPolicyEvaluator evaluator, ICurrentUser user)
    {
        _evaluator = evaluator;
        _user = user;
    }

    /// <summary>
    /// Evaluates the policy and applies the mandatory-block / warning-confirm / skip-audit rule.
    /// </summary>
    /// <param name="db">The handler's own context, so the skip audit commits in the SAME transaction.</param>
    /// <param name="entityCode">"Supplier" | "Asn" | "Invoice" (the AttachmentEntity code).</param>
    /// <param name="entityId">The instance id.</param>
    /// <param name="entityLabel">Human label for the audit detail (e.g. the ASN number).</param>
    /// <param name="supplierId">The instance's supplier (for the supplier-override tier).</param>
    /// <param name="acknowledgeMissing">The caller acknowledged proceeding past Warning-level requirements.</param>
    /// <param name="tenantId">Tenant to stamp on the audit row.</param>
    /// <param name="now">UTC timestamp.</param>
    /// <returns>
    /// <see cref="AttachmentSubmitDecision.Proceed"/> when the handler may continue (possibly after staging a skip
    /// audit), or a <c>RequiresConfirmation</c> decision the handler must surface without proceeding.
    /// </returns>
    public async Task<AttachmentSubmitDecision> EvaluateAsync(
        IAppDbContext db,
        string entityCode,
        Guid entityId,
        string entityLabel,
        Guid? supplierId,
        bool acknowledgeMissing,
        Guid? tenantId,
        DateTime now,
        CancellationToken ct)
    {
        var evaluation = await _evaluator.EvaluateAsync(entityCode, entityId, supplierId, ct);

        // 1) Mandatory first — block AND name what is missing (UC-ATT-02 / UC-ATT-05).
        if (evaluation.HasMissingMandatory)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["attachments"] = new[]
                {
                    "The following mandatory attachment(s) must be uploaded before you can proceed: "
                    + string.Join(", ", evaluation.MissingMandatory) + ".",
                },
            });

        // 2) Warning — confirm to proceed. Not an error: the caller surfaces a confirm prompt (UC-ATT-03).
        if (evaluation.HasMissingWarning && !acknowledgeMissing)
            return AttachmentSubmitDecision.Confirm(evaluation.MissingWarning);

        // 3) Proceed. If warnings were acknowledged-and-skipped, audit the skip (§8.4).
        if (evaluation.HasMissingWarning && acknowledgeMissing)
        {
            var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
            db.AuditEntries.Add(new AuditEntry
            {
                EntityName = entityCode,
                EntityId = entityId,
                Operation = SkipOp,
                FieldName = SkipField,
                OldValue = entityLabel,
                NewValue = Trunc("Skipped: " + string.Join(", ", evaluation.MissingWarning)),
                ChangedBy = actor,
                ChangedOn = now,
                TenantId = tenantId,
            });
        }

        return AttachmentSubmitDecision.Proceed;
    }

    // NewValue is nvarchar(max), but keep the skip summary bounded so a pathological 50-type policy can't bloat it.
    private static string Trunc(string s) => s.Length > 4000 ? s[..4000] : s;
}

/// <summary>
/// R4 (2026-06-26) — §8.3. The outcome of <see cref="AttachmentSubmitGuard.EvaluateAsync"/>. Either "proceed"
/// (the mandatory gate passed and any warnings were acknowledged or absent) or "requires confirmation" carrying
/// the Warning-level type names the supplier must confirm skipping. Mandatory failures never reach here — they
/// throw.
/// </summary>
public sealed record AttachmentSubmitDecision
{
    public bool RequiresConfirmation { get; init; }
    public IReadOnlyList<string> MissingWarning { get; init; } = Array.Empty<string>();

    public static readonly AttachmentSubmitDecision Proceed = new() { RequiresConfirmation = false };

    public static AttachmentSubmitDecision Confirm(IReadOnlyList<string> missingWarning)
        => new() { RequiresConfirmation = true, MissingWarning = missingWarning };
}
