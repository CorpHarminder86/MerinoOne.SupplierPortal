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

/// <summary>
/// One portal-entity → IDM entity-type mapping row (Settings › IDM Entity Type). <see cref="OwnerEntityType"/> is
/// the PORTAL entity (Asn / Invoice — resolved from the registered snapshot provider) whose documents of
/// <see cref="AttachmentType"/> are classified as <see cref="IdmEntityType"/>; null when no provider is registered
/// (the mapping can never dispatch).
/// </summary>
public record IdmAttachmentTypeConfigDto(
    Guid Id,
    string AttachmentType,
    string IdmEntityType,
    string? OwnerEntityType,
    IReadOnlyList<string> EligibilityGatePaths,
    string CreateMappingExpression,
    string? MutateMappingExpression,
    bool IsEnabled,
    bool IsDrifted,
    bool HasRepoDefault);

/// <summary>A selectable IDM entity type with the PORTAL entity its snapshot provider serves.</summary>
public record IdmEntityTypeOptionDto(string IdmEntityType, string OwnerEntityType);

/// <summary>Outcome of deleting a mapping row: whether it was removed + how many UNPUSHED documents were unclassified.</summary>
public record IdmConfigDeleteResultDto(bool Deleted, int ClearedDocuments);

public record SaveIdmAttachmentTypeConfigRequest(
    Guid? Id,
    string AttachmentType,
    string IdmEntityType,
    IReadOnlyList<string> EligibilityGatePaths,
    string CreateMappingExpression,
    string? MutateMappingExpression,
    bool IsEnabled);

public record IdmBackfillResultDto(int UpdatedCount);

/// <summary>An IDM transport endpoint (Integration › Endpoints). No auth/baseUrl — resolved from InforConnectionSetting (D3).</summary>
public record OutboundEndpointConfigDto(
    Guid Id,
    string EndpointKey,
    string HttpMethod,
    string RelativePath,
    string? StaticHeadersJson,
    string? AckParserKey,
    string? DefaultAcl,
    bool IsEnabled);

public record SaveOutboundEndpointConfigRequest(
    Guid? Id,
    string EndpointKey,
    string HttpMethod,
    string RelativePath,
    string? StaticHeadersJson,
    string? AckParserKey,
    string? DefaultAcl,
    bool IsEnabled);

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
