namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.4, D-R9-8/9) — evaluates a transaction type's JSONata eligibility gate against the
/// entity's input document. THE rule (exact): the gate applies iff a live config row exists AND its
/// dispatch mode is not Legacy AND its gate expression is non-blank — Legacy rows and absent configs
/// keep the legacy code-eligibility untouched. STRICT-TRUE: only a JSON <c>true</c> result is eligible;
/// false / undefined / non-boolean / evaluation error / unbuildable document are all ineligible with a
/// reason (fail closed, never throw to callers — a broken gate must never hang a business transaction).
/// </summary>
public interface ILnEligibilityService
{
    /// <summary>
    /// <paramref name="overrides"/> lets an IN-TRANSACTION caller inject facts the DB cannot see yet
    /// (the GRN-approval cascade evaluates coverage over its change-tracker state). Dispatch/sweep/backfill
    /// pass null — committed state is authoritative there.
    /// </summary>
    Task<LnGateVerdict> EvaluateAsync(Guid tenantId, string transactionType, Guid entityId,
        LnInputDocOverrides? overrides = null, CancellationToken ct = default);
}

/// <summary>
/// One gate evaluation. <c>HasGate=false</c> ⇒ the caller keeps its legacy eligibility (Eligible is true
/// so gated callers can branch on Eligible alone). <c>DispatchMode</c>/<c>GateVersion</c> ride along for
/// stamping (a Dynamic/Held row with a blank gate still stamps its gateVersion on enqueue).
/// </summary>
public sealed record LnGateVerdict(bool HasGate, bool Eligible, int? GateVersion, string? Reason, string? DispatchMode)
{
    public static readonly LnGateVerdict NoConfig = new(false, true, null, null, null);
}

/// <summary>In-transaction fact injection for gate evaluation (today: the invoice GRN-coverage bool).</summary>
public sealed record LnInputDocOverrides(bool? GrnCoverageSatisfied = null);
