using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm;

// R8 (2026-07-04) — TSD R8 §6. Application-layer contracts for the outbound IDM document-sync engine. The ONLY
// per-entity code is the snapshot assembly (IEntitySnapshotProvider); mapping, gating, ack parsing and transport
// are all generic. Kept in one file — these are small, cohesive seams consumed together by the dispatcher.

/// <summary>A request envelope — the JSONata mapping output. Body is raw JSON.</summary>
public sealed record OutboundEnvelope(IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Generic request builder: a pure JSONata evaluator — runs the given mapping expression against the assembled
/// snapshot to produce the <see cref="OutboundEnvelope"/>. NO per-entity mapping code (D-R8-8); config resolution
/// (which expression, static headers) stays with the caller.
/// </summary>
public interface IOutboundRequestBuilder
{
    Task<OutboundEnvelope> BuildAsync(string mappingExpression, object snapshot, CancellationToken ct);
}

/// <summary>fileUrl (storage key) → base64. The file fetch lives here (D-R8-18), never in JSONata; the base64
/// is injected into the snapshot at assembly time and never persisted in the outbox snapshot.</summary>
public interface IFileContentProvider
{
    /// <summary>Returns the base64 of the stored file, or null when the underlying bytes are missing (→ a Validation-class failure).</summary>
    Task<string?> ToBase64Async(string storageKey, CancellationToken ct);
}

/// <summary>
/// The only per-entity code: assembles the owning-entity snapshot graph (incl. <c>attachment.base64</c>) for one
/// IDM entity type. Implementations run with IgnoreQueryFilters + an explicit tenant predicate (background scope).
/// </summary>
public interface IEntitySnapshotProvider
{
    string IdmEntityType { get; }

    /// <summary>The <c>doc.DocumentUpload.OwnerEntityType</c> this provider assembles (for the seeding-scan join).</summary>
    string OwnerEntityType { get; }

    /// <summary>
    /// Assembles the snapshot, or null when the owning entity / document no longer resolves.
    /// <paramref name="includeFileContent"/> = false skips the (expensive) base64 file fetch — used for
    /// gate-only evaluation during seeding/promotion; = true injects <c>attachment.base64</c> for dispatch.
    /// </summary>
    Task<object?> BuildSnapshotAsync(Guid tenantId, Guid ownerEntityId, Guid documentUploadId, bool includeFileContent, CancellationToken ct);
}

/// <summary>Resolves the snapshot provider for an IDM entity type.</summary>
public interface ISnapshotProviderRegistry
{
    IEntitySnapshotProvider? TryGet(string idmEntityType);
    IReadOnlyCollection<IEntitySnapshotProvider> All { get; }
}

/// <summary>Evaluates the configured required-non-null snapshot paths (§4.3). Returns false if any is null/empty/missing.</summary>
public interface IEligibilityGate
{
    bool IsSatisfied(string eligibilityGateJson, object snapshot);
}

/// <summary>Validation classification of an IDM response (D-R8-23).</summary>
public enum IdmFailureClass { None, Transient, Validation }

/// <summary>Parsed IDM acknowledgement (XML; D-R8-21/22).</summary>
public sealed record IdmAck(
    string? Pid,          // <pid> …-LATEST form — the mutation handle
    string? Pid2,         // <pid2> GUID (captured, unused for mutation)
    string? Id,
    string? Version,
    IdmFailureClass Failure,
    string? Detail);      // <detail> for the sync log

/// <summary>Parses the IDM XML response (success <c>&lt;item&gt;</c> or <c>&lt;error&gt;</c>) and classifies the outcome.</summary>
public interface IIdmAckParser
{
    IdmAck Parse(int httpStatus, string xmlBody);
}

/// <summary>Raw transport result. <see cref="TransportFailure"/> = timeout/connection reset (→ Transient without parsing).</summary>
public sealed record IdmHttpResult(int StatusCode, string Body, bool TransportFailure);

/// <summary>
/// The IDM HTTP transport seam (Mock/Live, swapped by Integration:Mode — D8). R10: verb + path arrive
/// resolved from the unified <c>OutboundIntegrationConfig</c> row (the worker owns config resolution —
/// the transport just sends); <paramref name="operation"/> replaces the old endpointKey-suffix branching.
/// </summary>
public interface IIdmClient
{
    Task<IdmHttpResult> SendAsync(Guid tenantId, IdmOutboxOperation operation, string httpVerb, string path,
        OutboundEnvelope envelope, CancellationToken ct);
}

/// <summary>Compile-check a JSONata expression from the Application layer (Save-config validation) without a direct package ref.</summary>
public interface IJsonataValidator
{
    /// <summary>Returns null when the expression compiles, else the compile error message.</summary>
    string? Validate(string expression);
}

/// <summary>The repo-embedded default mapping expression for one IDM entity type (D6).</summary>
public sealed record IdmExpressionDefault(string IdmEntityType, string CreateExpression, string MutateExpression, string CreateHash, string MutateHash);

/// <summary>
/// Application-facing view of the repo-versioned JSONata catalogue (implemented in Infrastructure). Lets the
/// config query compute drift (row text hash ≠ repo default hash) and the restore-default command rewrite a row
/// from the repo — without an Application→Infrastructure reference.
/// </summary>
public interface IIdmExpressionCatalog
{
    IdmExpressionDefault? TryGet(string idmEntityType);

    /// <summary>Normalised SHA-256 (line endings folded) of an expression — MUST match the seeder's hashing.</summary>
    string Hash(string expression);
}
