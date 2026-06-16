using System.Security.Claims;
using System.Text.Encodings.Web;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MerinoOne.SupplierPortal.Identity.ApiKeyAuth;

/// <summary>
/// Authenticates inbound integration requests by the <c>X-APIKey</c> header. Runs BEFORE any
/// tenant/company context exists, so the key lookup uses <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/>.
/// On success a service principal is built carrying the key's tenant, bound company and scope→permission
/// claims; the existing PermissionPolicyProvider then enforces the per-endpoint scope policy.
///
/// All failure paths return a single generic message ("Invalid API key.") so a caller cannot distinguish
/// an unknown key from a revoked/expired one.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    /// <summary>
    /// Number of leading chars of the plaintext key stored as the non-secret lookup prefix. Keep in
    /// lock-step with CreateApiKeyCommand.PrefixLength. Long enough to be selective ("mok_" + 8 random
    /// chars), short enough to remain non-reversible.
    /// </summary>
    public const int PrefixLength = 12;

    // Skip the lastUsedAt write if the previous write was within this window — keeps a chatty integration
    // caller from hammering the row on every request.
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext db)
        : base(options, logger, encoder)
    {
        _db = db;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var headerValues))
            return AuthenticateResult.NoResult(); // no header → let the pipeline 401 cleanly

        var presented = headerValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(presented))
            return Fail();

        if (presented.Length < PrefixLength)
            return Fail();

        var prefix = presented[..PrefixLength];

        // Pre-tenant-context lookup: IgnoreQueryFilters drops tenant/company/soft-delete. Re-apply
        // !IsDeleted explicitly so a soft-deleted key never authenticates.
        var key = await _db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.KeyPrefix == prefix && k.IsActive && !k.IsDeleted)
            .FirstOrDefaultAsync();

        if (key is null)
            return Fail();

        // Constant-time hash compare BEFORE the cheap revoked/expired checks so timing does not leak
        // whether a given prefix exists with a valid secret.
        var hashMatches = ApiKeyHasher.Verify(presented, key.KeyHash);
        var now = DateTime.UtcNow;
        var revokedOrExpired = key.RevokedAt.HasValue || (key.ExpiresAt.HasValue && key.ExpiresAt.Value <= now);

        if (!hashMatches || revokedOrExpired)
            return Fail();

        var claims = new List<Claim>
        {
            new("userCode", $"apikey:{key.KeyPrefix}"),
            new(ClaimTypes.Name, key.Label),
            new("authmethod", "apikey"),
        };

        if (key.TenantId.HasValue)
            claims.Add(new Claim("tenant", key.TenantId.Value.ToString()));

        // Bound source company — the inbound write path checks the resolved company against this.
        if (key.TenantEntityId.HasValue)
            claims.Add(new Claim("tenantEntityId", key.TenantEntityId.Value.ToString()));

        // One permission claim per scope token so the existing PermissionPolicyProvider authorizes
        // [Authorize(AuthenticationSchemes="ApiKey", Policy="Integration.Inbound.PaymentTerm")] with no
        // new handler. Scopes are comma/space/semicolon delimited.
        foreach (var scope in ParseScopes(key.Scopes))
            claims.Add(new Claim("permission", scope));

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName, "userCode", ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        // Throttled fire-and-forget lastUsedAt update — telemetry only, must never fail the request.
        TouchLastUsed(key.Id, key.LastUsedAt, now);

        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid API key.");

    private static IEnumerable<string> ParseScopes(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes)) return Array.Empty<string>();
        return scopes
            .Split(new[] { ',', ' ', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Updates lastUsedAt at most once per <see cref="LastUsedThrottle"/>, on a detached context so it
    /// never participates in (or fails) the request's unit of work. Swallows all errors.
    /// </summary>
    private void TouchLastUsed(Guid keyId, DateTime? lastUsedAt, DateTime now)
    {
        if (lastUsedAt.HasValue && (now - lastUsedAt.Value) < LastUsedThrottle)
            return;

        var sp = Context.RequestServices;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.ApiKeys.IgnoreQueryFilters()
                    .Where(k => k.Id == keyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
            }
            catch
            {
                // best-effort telemetry — never surface
            }
        });
    }
}
