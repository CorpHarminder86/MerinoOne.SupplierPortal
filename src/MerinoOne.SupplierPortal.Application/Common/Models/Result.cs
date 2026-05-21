namespace MerinoOne.SupplierPortal.Application.Common.Models;

public class Result<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }

    public static Result<T> Ok(T data, string? traceId = null) => new() { Success = true, Data = data, TraceId = traceId };
    public static Result<T> Fail(params string[] errors) => new() { Success = false, Errors = errors.ToList() };
    public static Result<T> Fail(IEnumerable<string> errors) => new() { Success = false, Errors = errors.ToList() };
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
