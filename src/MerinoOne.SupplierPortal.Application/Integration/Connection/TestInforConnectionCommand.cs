using System.Text.RegularExpressions;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.Integration.Connection;

/// <summary>
/// Tests the supplied Infor settings by requesting an OAuth2 token. Returns a structured
/// <see cref="InforConnectionTestResult"/> (never throws) so the Settings UI can surface the message
/// verbatim. Port of the Outlook plugin's <c>testInforConnection</c>: required-field checks, an Infor
/// cloud-region cross-check between the SSO / ION API / C4ws URLs, then the live token request.
///
/// Secrets equal to <see cref="InforConnectionSecret.Mask"/> fall back to the stored (decrypted) values
/// so the operator can re-test without retyping credentials.
/// </summary>
public record TestInforConnectionCommand(TestInforConnectionRequest Body) : IRequest<InforConnectionTestResult>;

public class TestInforConnectionCommandHandler : IRequestHandler<TestInforConnectionCommand, InforConnectionTestResult>
{
    private static readonly Regex RegionRegex =
        new(@"\.([^.]+)\.inforcloudsuite\.com$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISettingProtector _protector;
    private readonly IInforConnectionTester _tester;
    private readonly ILogger<TestInforConnectionCommandHandler> _logger;

    public TestInforConnectionCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        ISettingProtector protector,
        IInforConnectionTester tester,
        ILogger<TestInforConnectionCommandHandler> logger)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _tester = tester;
        _logger = logger;
    }

    public async Task<InforConnectionTestResult> Handle(TestInforConnectionCommand request, CancellationToken ct)
    {
        var b = request.Body;

        // Resolve masked secrets against the stored row for this tenant.
        var stored = _user.TenantId is { } tid
            ? await _db.InforConnectionSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tid, ct)
            : null;

        var clientSecret = ResolveSecret(b.ClientSecret, stored?.ClientSecret);
        var password = ResolveSecret(b.Password, stored?.Password);

        var accessTokenUrl = b.AccessTokenUrl?.Trim() ?? string.Empty;
        var clientId = b.ClientId?.Trim() ?? string.Empty;
        var username = b.Username?.Trim() ?? string.Empty;
        var apiBaseUrl = b.ApiBaseUrl?.Trim() ?? string.Empty;
        var c4wsBaseUrl = b.IonC4wsBaseUrl?.Trim() ?? string.Empty;

        // Required-field checks (mirrors the plugin).
        var required = new (string Value, string Label)[]
        {
            (accessTokenUrl, "Access Token URL"),
            (clientId, "Client ID"),
            (clientSecret, "Client Secret"),
            (username, "Username"),
            (password, "Password"),
            (apiBaseUrl, "ION API Base URL"),
            // Company is optional — omitted from the required-field gate.
        };
        foreach (var (value, label) in required)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new InforConnectionTestResult(false, $"\"{label}\" is required.");
        }

        // Region cross-check — SSO, ION API and C4ws must all target the same Infor cloud region.
        var ssoRegion = ExtractRegion(accessTokenUrl);
        var apiRegion = ExtractRegion(apiBaseUrl);
        if (ssoRegion != null && apiRegion != null && !ssoRegion.Equals(apiRegion, StringComparison.OrdinalIgnoreCase))
        {
            return new InforConnectionTestResult(false,
                $"Region mismatch: Access Token URL uses \"{ssoRegion}\" but ION API Base URL uses \"{apiRegion}\". Both must target the same Infor environment.");
        }

        var c4wsRegion = ExtractRegion(c4wsBaseUrl);
        if (ssoRegion != null && c4wsRegion != null && !ssoRegion.Equals(c4wsRegion, StringComparison.OrdinalIgnoreCase))
        {
            return new InforConnectionTestResult(false,
                $"Region mismatch: Access Token URL uses \"{ssoRegion}\" but ION C4ws Base URL uses \"{c4wsRegion}\". Both must target the same Infor environment.");
        }

        try
        {
            var result = await _tester.RequestTokenAsync(
                new InforTokenRequest(accessTokenUrl, clientId, clientSecret, username, password), ct);
            return new InforConnectionTestResult(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Infor connection test failed.");
            return new InforConnectionTestResult(false, ex.Message);
        }
    }

    private string ResolveSecret(string? incoming, string? storedCipher)
    {
        if (string.Equals(incoming, InforConnectionSecret.Mask, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(storedCipher)) return string.Empty;
            try { return _protector.Unprotect(storedCipher); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stored Infor secret could not be unprotected (key-ring rotation?).");
                return string.Empty;
            }
        }
        return incoming ?? string.Empty;
    }

    private static string? ExtractRegion(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var m = RegionRegex.Match(uri.Host);
        return m.Success ? m.Groups[1].Value : null;
    }
}
