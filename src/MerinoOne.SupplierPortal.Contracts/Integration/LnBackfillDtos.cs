namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R9 (TSD R9 §2.5, D-R9-10/19) — backfill dry-run/apply DTOs.

/// <summary>One affected row in a backfill preview set.</summary>
public sealed record LnBackfillRowDto(
    Guid? OutboxMessageId,
    Guid EntityId,
    string DeterministicKey,
    string? CurrentStatus,
    string? Reason);

/// <summary>The mandatory dry-run preview: the three delta sets with row lists + the untouchable tails.</summary>
public sealed record LnBackfillPreviewDto(
    Guid RunId,
    Guid ConfigId,
    string TransactionType,
    int GateVersion,
    IReadOnlyList<LnBackfillRowDto> Enqueue,
    IReadOnlyList<LnBackfillRowDto> Rearm,
    IReadOnlyList<LnBackfillRowDto> Withdraw,
    int SendingInFlight,
    int PostedImmutable,
    DateTime ComputedAt);

/// <summary>Apply outcome incl. the race-visibility counters (nothing about a backfill is ever silent).</summary>
public sealed record LnBackfillApplyResultDto(
    Guid RunId,
    int Enqueued,
    int Rearmed,
    int Withdrawn,
    int RacedAway,
    int EscapedToSending,
    int AlreadyLive);

/// <summary>Feeds the D-R9-19 auto-prompt: a gateVersion bump since the last applied run prompts a dry-run.</summary>
public sealed record LnBackfillStatusDto(
    Guid ConfigId,
    string TransactionType,
    int ConfigGateVersion,
    int? LastAppliedGateVersion,
    DateTime? LastDryRunAt,
    Guid? LatestDryRunId,
    bool PromptDryRun);

/// <summary>History row for the backfill screen.</summary>
public sealed record LnBackfillRunDto(
    Guid Id,
    string TransactionType,
    int GateVersion,
    string Status,
    int EnqueueCount,
    int RearmCount,
    int WithdrawCount,
    DateTime CreatedOn,
    string CreatedBy,
    DateTime? AppliedOn,
    string? AppliedBy);
