namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.4 enqueue point + D-R9-10a) — the gated outbox enqueue every LN transaction site uses:
/// evaluates the config gate (when one applies), then INSERTS a Pending row, RE-ARMS an existing
/// Skipped/Failed row on the same deterministic key in place (the filtered unique index spans all
/// statuses, so re-arm-over-create is the only correct move — this is what makes revoke → edit →
/// resubmit work, O-R9-4), or no-ops on a live row. Gate-ineligible creates NOTHING (deliberate
/// divergence from IDM's Blocked-row model — the reconciliation sweep catches later-eligible entities).
/// Held mode still enqueues: the kill switch stops dispatch, never enqueue (D-R9-11).
/// Joins the CALLER's unit of work — the row/re-arm commits atomically with the business change.
/// </summary>
public interface ILnGatedOutboxEnqueuer
{
    Task<GatedEnqueueResult> EnqueueAsync(
        string transactionType,
        string entityName,
        Guid? entityId,
        string deterministicKey,
        string? payloadJson,
        Guid? tenantIdOverride = null,
        LnInputDocOverrides? overrides = null,
        CancellationToken ct = default);
}

public enum GatedEnqueueOutcome
{
    /// <summary>No config gate applied (absent row or Legacy mode) — enqueued exactly like the legacy path.</summary>
    EnqueuedLegacy,
    /// <summary>Gate passed — new Pending row with gateVersion stamped.</summary>
    Enqueued,
    /// <summary>An existing Skipped/Failed row on the same key was re-armed in place (D-R9-10a).</summary>
    Rearmed,
    /// <summary>A live row (Pending/Sending/Dispatched/Acked) already owns the key — no-op.</summary>
    AlreadyLive,
    /// <summary>The gate said no — NO row exists; the business change proceeds, posting is config-suppressed.</summary>
    GateIneligible,
}

public sealed record GatedEnqueueResult(GatedEnqueueOutcome Outcome, int? GateVersion, string? Reason);
