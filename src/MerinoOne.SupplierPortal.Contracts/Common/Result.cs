namespace MerinoOne.SupplierPortal.Contracts.Common;

public class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }
}

public class ApiResult
{
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? TraceId { get; init; }
}
