namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R8 (2026-07-04) — TSD R8. Wire shapes for the Infor IDM document-sync UI + API. Named Idm* to avoid the
// existing InforSyncLogDto collision (Contracts.Shipments + Contracts.Integration). Web consumes Contracts only.

/// <summary>One IDM document-outbox row for the sync-log list (RLS-scoped). Blobs fetched separately on demand.</summary>
public record IdmSyncLogDto(
    Guid Id,
    int Seq,
    Guid DocumentUploadId,
    string IdmEntityType,
    Guid OwnerEntityId,
    string FileName,
    string Operation,
    string Status,
    int AttemptCount,
    DateTime? NextAttemptAt,
    string? Pid,
    string? LastError,
    DateTime CreatedOn,
    DateTime? UpdatedOn,
    bool HasRequestSnapshot,
    bool HasResponse,
    string? SupplierCode,
    string? SupplierName,
    string? MimeType,
    long? FileSizeKb);

/// <summary>On-demand detail for one sync-log row: the elided request snapshot + the raw IDM (XML) response.</summary>
public record IdmSyncLogDetailDto(string? RequestSnapshotJson, string? ResponseBody);

// R10 — the R8 config DTO pair (attachment-type + transport endpoint) is retired: Document-kind rows on the
// unified OutboundIntegrationConfigDto carry mapping + routing in one shape.

/// <summary>A selectable IDM entity type with the PORTAL entity its snapshot provider serves (autocomplete hint).</summary>
public record IdmEntityTypeOptionDto(string IdmEntityType, string OwnerEntityType);

public record IdmBackfillResultDto(int UpdatedCount);

public record ValidateOutboundEndpointResultDto(bool Success, bool TokenOk, bool ReachabilityOk, int HttpStatus, string Message);

public record IdmTestBenchRequest(Guid DocumentUploadId, bool DryRun);

public record IdmTestBenchResultDto(
    bool GateSatisfied,
    IReadOnlyList<string> GateFailures,
    string HeadersJson,
    string BodyJson,
    int? DryRunStatus,
    string? DryRunResponse);

/// <summary>A candidate document for the test bench picker.</summary>
public record IdmDocumentPickDto(Guid DocumentUploadId, string FileName, string DocumentType, string OwnerEntityType, string? IdmEntityType, bool HasPid);
