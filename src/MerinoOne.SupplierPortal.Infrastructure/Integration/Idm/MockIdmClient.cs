using System.Security.Cryptography;
using System.Text;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §6 / D8. Deterministic Mock IDM transport for dev + tests (Integration:Mode != Live).
/// Create/Update return a 200 <c>&lt;item&gt;</c> with a pid derived from the request body (stable per payload);
/// Delete returns 200. Test hooks: a filename/body containing <c>idm-fail-validation</c> returns a 400
/// <c>&lt;error&gt;</c>; <c>idm-fail-transient</c> returns a 500 — so the dispatch state machine is exercisable
/// without a live endpoint. (No failure-hook precedent in MockInforIntegrationService — this is new, test-only behaviour.)
/// </summary>
public sealed class MockIdmClient : IIdmClient
{
    private const string DafNs = "http://infor.com/daf";
    private readonly ILogger<MockIdmClient> _logger;

    public MockIdmClient(ILogger<MockIdmClient> logger) => _logger = logger;

    public Task<IdmHttpResult> SendAsync(Guid tenantId, string endpointKey, OutboundEnvelope envelope, CancellationToken ct)
    {
        var body = envelope.Body ?? string.Empty;

        if (body.Contains("idm-fail-validation", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new IdmHttpResult(400,
                $"<error xmlns=\"{DafNs}\"><detail>File name \"null\"</detail></error>", false));

        if (body.Contains("idm-fail-transient", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new IdmHttpResult(500,
                $"<error xmlns=\"{DafNs}\"><detail>Simulated transient IDM failure.</detail></error>", false));

        if (endpointKey.EndsWith(".Delete", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new IdmHttpResult(200, $"<item xmlns=\"{DafNs}\"><status>deleted</status></item>", false));

        var handle = StableHandle(body);
        var pid = $"MDS-{handle}-LATEST";
        var xml =
            $"<item xmlns=\"{DafNs}\"><pid>{pid}</pid><pid2>{Guid.Empty}</pid2><id>MDS-{handle}</id><version>1</version></item>";
        _logger.LogDebug("[IDM Mock] {Endpoint} → pid {Pid}", endpointKey, pid);
        return Task.FromResult(new IdmHttpResult(200, xml, false));
    }

    private static string StableHandle(string body)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
