using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MerinoOne.Web.Services;

public static class HttpResponseExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static async Task<T?> ReadResultAsync<T>(this HttpResponseMessage resp, CancellationToken ct = default)
    {
        await EnsureSuccessOrThrowAsync(resp, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || resp.Content.Headers.ContentLength == 0)
            return default;
        try { return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct); }
        catch (JsonException) { return default; }
    }

    public static async Task EnsureSuccessOrThrowAsync(this HttpResponseMessage resp, CancellationToken ct = default)
    {
        if (resp.IsSuccessStatusCode) return;

        var statusCode = (int)resp.StatusCode;
        string title = $"Request failed ({statusCode})";
        string[] errors = Array.Empty<string>();
        string? traceId = null;

        string body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        title = t.GetString()!;
                    else if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                        title = d.GetString()!;
                    else if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                        title = m.GetString()!;

                    if (root.TryGetProperty("errors", out var errs))
                    {
                        if (errs.ValueKind == JsonValueKind.Array)
                        {
                            errors = errs.EnumerateArray()
                                .Where(e => e.ValueKind == JsonValueKind.String)
                                .Select(e => e.GetString()!)
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();
                        }
                        else if (errs.ValueKind == JsonValueKind.Object)
                        {
                            errors = errs.EnumerateObject()
                                .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array
                                    ? p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
                                    : Enumerable.Empty<string>())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToArray();
                        }
                    }

                    if (errors.Length == 0 && root.TryGetProperty("Errors", out var errsPascal) && errsPascal.ValueKind == JsonValueKind.Array)
                    {
                        errors = errsPascal.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToArray();
                    }

                    if (root.TryGetProperty("traceId", out var tr) && tr.ValueKind == JsonValueKind.String)
                        traceId = tr.GetString();
                    else if (root.TryGetProperty("TraceId", out var trPascal) && trPascal.ValueKind == JsonValueKind.String)
                        traceId = trPascal.GetString();
                }
            }
            catch (JsonException) { /* not JSON; keep defaults */ }
        }

        throw new ApiException(statusCode, title, errors, traceId);
    }
}
