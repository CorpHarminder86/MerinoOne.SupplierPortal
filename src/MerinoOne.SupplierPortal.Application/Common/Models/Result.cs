namespace MerinoOne.SupplierPortal.Application.Common.Models;

public class Result<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }

    // R4 (2026-06-26) — TSD R4 Addendum §8.3, Component 5 (Attachment Requirement Governance). The two-step
    // "Warning attachment missing → confirm to proceed" flow. When ConfirmationRequired is true the request was
    // NOT applied (Data is null): the client shows ConfirmationMessage + MissingAttachments and re-submits with
    // AcknowledgeMissingAttachments=true. This is a NORMAL flow flag on a 200 response, NOT an error — Success
    // stays false (nothing was committed) but Errors is empty (it is not a failure).
    public bool ConfirmationRequired { get; init; }
    public string? ConfirmationMessage { get; init; }
    public List<string> MissingAttachments { get; init; } = new();

    // R6 (2026-07-02) — ADVISORY notes on a SUCCESSFUL response (e.g. invoice-submit tax-rate drift:
    // "Tax GST18: rate changed 18% → 12%"). Informational only — Success stays true, Errors stays empty; the
    // client surfaces them as toasts. Empty when there is nothing to say.
    public List<string> Notices { get; init; } = new();

    public static Result<T> Ok(T data, string? traceId = null) => new() { Success = true, Data = data, TraceId = traceId };
    public static Result<T> Fail(params string[] errors) => new() { Success = false, Errors = errors.ToList() };
    public static Result<T> Fail(IEnumerable<string> errors) => new() { Success = false, Errors = errors.ToList() };

    /// <summary>
    /// R4 — maps an attachment-Warning confirmation to a 200 response carrying the prompt + the missing Warning
    /// type names. Not an error (Errors empty); the client re-submits with the acknowledge flag to proceed.
    /// </summary>
    public static Result<T> NeedsConfirmation(string message, IEnumerable<string> missing, string? traceId = null) => new()
    {
        Success = false,
        ConfirmationRequired = true,
        ConfirmationMessage = message,
        MissingAttachments = missing.ToList(),
        TraceId = traceId,
    };
}

public class Result
{
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }

    public static Result Ok(string? traceId = null) => new() { Success = true, TraceId = traceId };
    public static Result Fail(params string[] errors) => new() { Success = false, Errors = errors.ToList() };
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}
