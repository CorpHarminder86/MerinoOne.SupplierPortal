using System.Net.Http.Headers;
using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 — the HTTP leg of the dynamic LN path. Extracted from <c>LiveInforIntegrationService.SendAsync</c>
/// plumbing with two deliberate differences: (1) the RESPONSE BODY is captured on success too (the
/// response mapping needs it — Live discards it today), and (2) tenant-explicit connection/token
/// resolution (the dispatcher drains all tenants with no ambient <c>ICurrentUser</c>). Uses the named
/// <c>"ln"</c> HttpClient via <see cref="IHttpClientFactory"/> so tests can substitute the handler
/// (the legacy static HttpClient pattern is not copied).
/// </summary>
public interface ILnHttpTransport
{
    Task<LnHttpOutcome> SendAsync(Guid tenantId, string httpVerb, string relativePath, string bodyJson, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// One HTTP attempt's outcome. <c>StatusCode</c> null = the request never got an HTTP answer
/// (pre-flight config failure, timeout, transport error) — always retriable per D-R9-5.
/// </summary>
public sealed record LnHttpOutcome(int? StatusCode, string? ResponseBody, string? Error)
{
    public bool IsHttpSuccess => StatusCode is >= 200 and < 300;
}

public sealed class LnHttpTransport : ILnHttpTransport
{
    public const string HttpClientName = "ln";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IInforConnectionProvider _connections;
    private readonly IInforTokenProvider _tokens;
    private readonly ILogger<LnHttpTransport> _logger;

    public LnHttpTransport(
        IHttpClientFactory httpFactory,
        IInforConnectionProvider connections,
        IInforTokenProvider tokens,
        ILogger<LnHttpTransport> logger)
    {
        _httpFactory = httpFactory;
        _connections = connections;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<LnHttpOutcome> SendAsync(Guid tenantId, string httpVerb, string relativePath, string bodyJson, string idempotencyKey, CancellationToken ct = default)
    {
        var conn = await _connections.GetForTenantAsync(tenantId, ct);
        if (conn is null || !conn.IsActive)
            return new LnHttpOutcome(null, null, "Infor connection is not configured (or is disabled) for this tenant.");
        if (!conn.IsConfigured)
            return new LnHttpOutcome(null, null, "Infor connection is incomplete — set Access Token URL, ION API Base URL and Company in Settings.");

        var token = await _tokens.GetAccessTokenAsync(tenantId, ct);
        if (string.IsNullOrEmpty(token))
            return new LnHttpOutcome(null, null, "Could not obtain an Infor access token — re-test the connection in Settings.");

        if (!TryBuildUrl(conn.ApiBaseUrl, relativePath, out var url))
            return new LnHttpOutcome(null, null, $"Could not build a valid endpoint URL from ION API Base URL '{conn.ApiBaseUrl}'.");

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(httpVerb), url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(conn.PrimaryCompany))
                req.Headers.TryAddWithoutValidation("X-Infor-LnCompany", conn.PrimaryCompany);
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new LnHttpOutcome((int)resp.StatusCode, body, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LnHttpOutcome(null, null, "Infor request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "LN dynamic request failed (transport) for tenant {TenantId}.", tenantId);
            return new LnHttpOutcome(null, null, $"Could not reach Infor: {ex.Message}");
        }
    }

    /// <summary>Joins the configured ION API base URL with a relative path (tolerant of trailing/leading slashes) — verbatim from LiveInforIntegrationService.</summary>
    private static bool TryBuildUrl(string apiBaseUrl, string relativePath, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(apiBaseUrl?.Trim(), UriKind.Absolute, out var baseUri)) return false;
        var basePart = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var rel = relativePath.Trim().TrimStart('/');
        url = $"{basePart}/{rel}";
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
}
