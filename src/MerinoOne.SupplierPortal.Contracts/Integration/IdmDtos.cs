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
/// One (portal-entity, attachment-type) → IDM entity-type mapping row (Settings › IDM Entity Type).
/// <see cref="OwnerEntityType"/> is the PORTAL entity (Asn / Invoice / Supplier) whose documents this row
/// classifies as <see cref="IdmEntityType"/>. <see cref="AttachmentType"/> is OPTIONAL: null = catch-all (every
/// document of the portal entity). <see cref="IdmEntityType"/> is free text — a value with no registered snapshot
/// provider leaves the mapping dormant (nothing dispatches) until one is added.
/// </summary>
public record IdmAttachmentTypeConfigDto(
    Guid Id,
    string? AttachmentType,
    string IdmEntityType,
    string? OwnerEntityType,
    // R9 (§2.11) — JSONata boolean expression (was a dot-path list); one gate language across IDM and LN.
    string EligibilityGateExpr,
    string CreateMappingExpression,
    string? MutateMappingExpression,
    bool IsEnabled,
    bool IsDrifted,
    bool HasRepoDefault);

/// <summary>A selectable IDM entity type with the PORTAL entity its snapshot provider serves (autocomplete hint).</summary>
public record IdmEntityTypeOptionDto(string IdmEntityType, string OwnerEntityType);

/// <summary>Outcome of deleting a mapping row: whether it was removed + how many UNPUSHED documents were unclassified.</summary>
public record IdmConfigDeleteResultDto(bool Deleted, int ClearedDocuments);

public record SaveIdmAttachmentTypeConfigRequest(
    Guid? Id,
    string OwnerEntityType,
    string? AttachmentType,
    string IdmEntityType,
    // R9 (§2.11) — JSONata boolean expression; blank = never satisfied (fail closed, as the empty path list was).
    string EligibilityGateExpr,
    string CreateMappingExpression,
    string? MutateMappingExpression,
    bool IsEnabled);

public record IdmBackfillResultDto(int UpdatedCount);

/// <summary>An IDM transport endpoint (Integration › Endpoints). No auth/baseUrl — resolved from InforConnectionSetting (D3).
/// <see cref="DefaultAcl"/>/<see cref="EntityName"/> on the tenant's <c>IDM.Item.Create</c> row are read by the
/// snapshot providers into every mapping expression's <c>config.acl</c>/<c>config.entityName</c>.</summary>
public record OutboundEndpointConfigDto(
    Guid Id,
    string EndpointKey,
    string HttpMethod,
    string RelativePath,
    string? StaticHeadersJson,
    string? AckParserKey,
    string? DefaultAcl,
    string? EntityName,
    bool IsEnabled);

public record SaveOutboundEndpointConfigRequest(
    Guid? Id,
    string EndpointKey,
    string HttpMethod,
    string RelativePath,
    string? StaticHeadersJson,
    string? AckParserKey,
    string? DefaultAcl,
    string? EntityName,
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
