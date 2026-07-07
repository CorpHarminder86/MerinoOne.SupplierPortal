using System.Net.Http.Headers;
using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §6 / D3+D8. Live IDM transport over the ION gateway. R10: verb + path arrive
/// resolved from the unified <c>OutboundIntegrationConfig</c> row (the worker owns config resolution and the
/// Held gate — the old per-row IsEnabled lookup is gone); this class only resolves the tenant's ApiBaseUrl +
/// OAuth bearer via the tenant-explicit provider overloads (the worker has no ICurrentUser context) and sends.
/// Timeouts / connection resets are reported as <see cref="IdmHttpResult.TransportFailure"/> = true (→ Transient).
/// </summary>
public sealed class LiveIdmClient : IIdmClient
{
    private readonly IInforConnectionProvider _connections;
    private readonly IInforTokenProvider _tokens;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LiveIdmClient> _logger;

    public LiveIdmClient(IInforConnectionProvider connections, IInforTokenProvider tokens,
        IHttpClientFactory httpFactory, ILogger<LiveIdmClient> logger)
    {
        _connections = connections;
        _tokens = tokens;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IdmHttpResult> SendAsync(Guid tenantId, IdmOutboxOperation operation, string httpVerb, string path,
        OutboundEnvelope envelope, CancellationToken ct)
    {
        var conn = await _connections.GetForTenantAsync(tenantId, ct);
        if (conn is null || !conn.IsActive || !conn.IsConfigured)
            return new IdmHttpResult(0, "Tenant Infor connection is not configured/active.", false);

        var token = await _tokens.GetAccessTokenAsync(tenantId, ct);
        if (string.IsNullOrEmpty(token))
            return new IdmHttpResult(0, "Could not acquire an Infor OAuth token for the tenant.", true);

        // The tenant's ApiBaseUrl may be LN-suite-scoped (…/{tenant}/LN/lnapi) while IDM lives at the tenant
        // root (…/{tenant}/IDM/api/items). An ABSOLUTE path (http/https) is used verbatim, so the IDM
        // endpoint can be configured independently of the LN base without touching InforConnectionSetting.
        var url = CombineUrl(conn.ApiBaseUrl, path);
        using var req = new HttpRequestMessage(new HttpMethod(httpVerb), url)
        {
            Content = new StringContent(envelope.Body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (k, v) in envelope.Headers)
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // set on the content
            req.Headers.TryAddWithoutValidation(k, v);
        }

        try
        {
            var client = _httpFactory.CreateClient("idm");
            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new IdmHttpResult((int)resp.StatusCode, body, false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IdmHttpResult(0, "IDM request timed out.", true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[IDM Live] transport failure ({Verb} {Path}).", httpVerb, path);
            return new IdmHttpResult(0, ex.Message, true);
        }
    }

    internal static string CombineUrl(string baseUrl, string relativePath)
        => relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
}
