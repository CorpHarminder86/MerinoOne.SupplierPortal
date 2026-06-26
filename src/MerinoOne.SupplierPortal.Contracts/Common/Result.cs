namespace MerinoOne.SupplierPortal.Contracts.Common;

public class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }

    // R4 (2026-06-26) — Phase 5b / TSD R4 Addendum §8.3 (Attachment Requirement Governance). The two-step
    // "Warning attachment missing → confirm to proceed" flow. The API's Application-layer Result already serialises
    // these on a 200 response (NOT an error): when ConfirmationRequired is true nothing was committed — the client
    // shows ConfirmationMessage + MissingAttachments and re-submits with AcknowledgeMissingAttachments=true. The Web
    // deserialises into this envelope, so the fields must live here too or they are silently dropped. Additive,
    // init-only, default false/empty so existing callers are unaffected. Mandatory-missing comes back as a normal
    // 400 in Errors (handled the existing way) — these three only carry the Warning confirm path.
    public bool ConfirmationRequired { get; init; }
    public string? ConfirmationMessage { get; init; }
    public List<string> MissingAttachments { get; init; } = new();
}

public class ApiResult
{
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }

    // R4 (2026-06-26) — see ApiResult<T>. Mirrored on the non-generic envelope for symmetry / future submit paths
    // that return no payload.
    public bool ConfirmationRequired { get; init; }
    public string? ConfirmationMessage { get; init; }
    public List<string> MissingAttachments { get; init; } = new();
}
