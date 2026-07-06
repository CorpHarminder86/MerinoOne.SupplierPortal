using System.Text.Json;

namespace MerinoOne.SupplierPortal.Contracts.Integration;

// TSD R9 — LN outbound endpoint-config DTOs (Phase A). The closed response/ack contract and the
// admin-screen CRUD/validate/attest/pin surface for integration.LnEndpointConfig.

/// <summary>
/// The fixed CLOSED portal contract every response/ack mapping must produce (D-R9-4):
/// <c>{ erpKey, erpStatus, message?, correlationBag? }</c>. Unknown keys block config save.
/// <c>erpStatus</c> is written ONLY to the owning entity's existing ERP-owned status column
/// (D-R9-20 — today that is <c>PurchaseOrder.ErpStatus</c>; entities without one keep the value in
/// the sync log). It never touches a portal workflow status or the outbox status.
/// </summary>
public sealed record LnOutboundAck(
    string ErpKey,
    string ErpStatus,
    string? Message = null,
    JsonElement? CorrelationBag = null);

/// <summary>List/edit projection of one <c>integration.LnEndpointConfig</c> row.</summary>
public sealed record LnEndpointConfigDto(
    Guid Id,
    int Seq,
    string TransactionType,
    string PortalEntity,
    string EndpointPath,
    string HttpVerb,
    string DispatchMode,
    string? EligibilityGateExpr,
    string RequestMappingExpr,
    string ResponseMappingExpr,
    string? AckMappingExpr,
    string? CandidateFilterName,
    string? CandidateFilterParams,
    int GateVersion,
    bool HasSample,
    string? SampleBuilderVersion,
    bool SampleStale,
    string? SampleDocumentJson,
    string? ResponseSampleJson,
    string? AckSampleJson,
    bool RequestDrifted,
    bool ResponseDrifted,
    bool AckDrifted,
    string? VerifiedBy,
    DateTime? VerifiedAt,
    string? VerifiedNote,
    bool PathConfirmed,
    DateTime CreatedOn,
    DateTime? UpdatedOn);

/// <summary>Create/update request. Never carries dispatchMode — mode transitions are a separate audited action.</summary>
public sealed record SaveLnEndpointConfigRequest(
    Guid? Id,
    string TransactionType,
    string PortalEntity,
    string EndpointPath,
    string HttpVerb,
    string? EligibilityGateExpr,
    string RequestMappingExpr,
    string ResponseMappingExpr,
    string? AckMappingExpr,
    string? CandidateFilterName,
    string? CandidateFilterParams,
    string? ResponseSampleJson,
    string? AckSampleJson);

/// <summary>Dry validation of a config shape (same pipeline as save, no write). Reused by the Phase D editor live-eval.</summary>
public sealed record ValidateLnEndpointConfigRequest(
    string PortalEntity,
    string? EligibilityGateExpr,
    string? RequestMappingExpr,
    string? ResponseMappingExpr,
    string? AckMappingExpr,
    string? CandidateFilterName,
    string? CandidateFilterParams,
    string? SampleDocumentJson,
    string? ResponseSampleJson,
    string? AckSampleJson);

/// <summary>Per-slot validation outcome. <c>RenderedRequestJson</c> = request expression evaluated against the sample.</summary>
public sealed record LnConfigValidationResultDto(
    bool IsValid,
    IReadOnlyList<string> GateErrors,
    IReadOnlyList<string> RequestErrors,
    IReadOnlyList<string> ResponseErrors,
    IReadOnlyList<string> AckErrors,
    IReadOnlyList<string> GeneralErrors,
    string? RenderedRequestJson);

/// <summary>
/// Attestation (D-R9-17 + D-R9-21). <c>PathConfirmed</c> is the hard confirm checkbox
/// ("endpoint path confirmed against tenant Available-APIs export") — ticking it stamps the
/// confirmation line into <c>verifiedNote</c>; it gates DispatchMode → Dynamic.
/// </summary>
public sealed record AttestLnEndpointRequest(string Note, bool PathConfirmed);

/// <summary>Mode transition: <c>Legacy</c> | <c>Dynamic</c> | <c>Held</c>. → Dynamic requires attestation + pathConfirmed + fresh pinned sample + green validation.</summary>
public sealed record SetLnDispatchModeRequest(string Mode);

/// <summary>Pin request (D-R9-18): run this entity through the input-document builder and freeze the output on the config.</summary>
public sealed record PinLnSampleRequest(Guid EntityId);

/// <summary>Sample-candidate row for the pin picker (RLS-scoped recent entities of the config's portalEntity).</summary>
public sealed record LnSampleCandidateDto(Guid EntityId, string DisplayKey, string? StatusLabel, DateTime CreatedOn);

/// <summary>Registry-backed candidate filter (D-R9-15) for the config dropdown. Unknown names cannot save.</summary>
public sealed record LnCandidateFilterDto(string PortalEntity, string Name, bool IsParameterized, string? ParamsHint);

/// <summary>Restore one expression slot (<c>request</c>|<c>response</c>|<c>ack</c>|<c>gate</c>) to the repo default.</summary>
public sealed record RestoreLnDefaultExpressionRequest(string Slot);
