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

/// <summary>
/// An Infor endpoint "session": the configuration of one inbound/outbound channel plus its liveness
/// telemetry (last received timestamp/status/idempotency-key/message + cumulative received count).
/// </summary>
public record InforEndpointDto(
    Guid Id,
    int Seq,
    string EntityName,
    string Direction,
    string InforEndpointUrl,
    string? BodName,
    bool IsEnabled,
    DateTime? LastReceivedAt,
    string? LastStatus,
    string? LastIdempotencyKey,
    string? LastMessage,
    int ReceivedCount,
    DateTime CreatedOn);

/// <summary>Tenant-Admin: update an endpoint's config (URL, BOD name, enabled flag).</summary>
public record UpdateInforEndpointRequest(
    string InforEndpointUrl,
    string? BodName,
    bool IsEnabled);
