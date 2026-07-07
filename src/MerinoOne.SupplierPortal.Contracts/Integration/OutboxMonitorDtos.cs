namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R9 — outbox monitoring surface (Skipped visibility incl. reason + gateVersion, errorClass badge, re-arm).

public sealed record OutboxMessageDto(
    Guid Id,
    string TransactionType,
    string EntityName,
    Guid? EntityId,
    string DeterministicKey,
    string Status,
    int AttemptCount,
    int? GateVersion,
    string? SkipReason,
    string? ErrorClass,
    string? LastError,
    DateTime CreatedOn,
    DateTime? DispatchedAt,
    DateTime? AckedAt);

public sealed record OutboxMessagePageDto(int Total, IReadOnlyList<OutboxMessageDto> Rows);

/// <summary>Re-arm outcome. <c>WasPermanent</c> = the admin overrode a permanent-classified failure (warned in the UI).</summary>
public sealed record RearmOutboxResultDto(bool Rearmed, bool WasPermanent, string? Message);

/// <summary>R10 — on-demand payload detail for one outbox row: enqueue args + the latest dispatch attempt's
/// request payload (InforSyncLog, linked by deterministic key) and its outcome.</summary>
public sealed record OutboxMessageDetailDto(
    Guid Id,
    string TransactionType,
    string DeterministicKey,
    string? ArgsJson,
    string? RequestPayloadJson,
    string? AttemptStatus,
    DateTime? AttemptAt,
    string? ResponseInfo);
