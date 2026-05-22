namespace MerinoOne.SupplierPortal.Contracts.Integration;

public record InforSyncLogDto(
    Guid Id,
    int Seq,
    string EntityName,
    string Direction,
    string Status,
    string? PayloadRef,
    string? IdempotencyKey,
    DateTime SyncedAt,
    string? ErrorMessage);

public record IntegrationErrorDto(
    Guid Id,
    int Seq,
    Guid? SyncLogId,
    string EntityName,
    string ErrorMessage,
    int RetryCount,
    DateTime? LastRetriedAt,
    bool IsResolved,
    string? ResolutionNote,
    DateTime CreatedOn);
