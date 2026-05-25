namespace MerinoOne.Web.Services;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public string Title { get; }
    public string[] Errors { get; }
    public string? TraceId { get; }

    public ApiException(int statusCode, string title, string[] errors, string? traceId)
        : base(title)
    {
        StatusCode = statusCode;
        Title = title;
        Errors = errors ?? Array.Empty<string>();
        TraceId = traceId;
    }
}
