using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Low-level OAuth2 password-grant token request against Infor Mingle SSO. Shared by the Settings
/// "Test connection" path (<see cref="InforConnectionTester"/>) and the live outbound integration
/// (<c>InforTokenProvider</c>) so the request shape lives in exactly one place.
///
/// Client credentials go in the HTTP Basic header (RFC 6749 §2.3.1 — Infor returns 500 server_error
/// if they are in the body); the grant is <c>grant_type=password</c> with the service-account
/// username/password in the form body. Stateless — safe to register as a singleton.
/// </summary>
public class InforOAuthTokenClient
{
    // Single shared HttpClient (recommended pattern — avoids socket exhaustion). Per-request
    // auth/headers are set on the HttpRequestMessage, never on DefaultRequestHeaders.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<InforOAuthResult> RequestAsync(
        string accessTokenUrl,
        string clientId,
        string clientSecret,
        string username,
        string password,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(accessTokenUrl?.Trim(), UriKind.Absolute, out var tokenUri)
            || (tokenUri.Scheme != Uri.UriSchemeHttps && tokenUri.Scheme != Uri.UriSchemeHttp))
        {
            return InforOAuthResult.Fail("Access Token URL is not a valid absolute URL.");
        }

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            msg.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
            });

            using var response = await Http.SendAsync(msg, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isHtml = contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);

            if (!response.IsSuccessStatusCode)
            {
                if (isHtml)
                {
                    return InforOAuthResult.Fail(response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "Token endpoint returned a 404 — check that your Access Token URL is correct and the tenant name matches your environment."
                        : $"Token endpoint returned an HTML error page (HTTP {(int)response.StatusCode}). The Access Token URL may be incorrect.");
                }
                var detail = body.Length > 200 ? body[..200] : body;
                return InforOAuthResult.Fail(
                    $"Token request failed ({(int)response.StatusCode}): {(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail)}");
            }

            if (isHtml)
            {
                return InforOAuthResult.Fail("Token endpoint returned HTML instead of JSON. The Access Token URL may be incorrect.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var token) || string.IsNullOrWhiteSpace(token.GetString()))
            {
                return InforOAuthResult.Fail("Response did not contain an access token.");
            }

            int? expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var v) ? v : null;
            var msgText = expiresIn is { } secs
                ? $"Connected successfully. Token expires in {secs} seconds."
                : "Connected successfully.";
            return InforOAuthResult.Ok(token.GetString()!, expiresIn, msgText);
        }
        catch (JsonException)
        {
            return InforOAuthResult.Fail("Token endpoint returned a response that could not be parsed as JSON.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return InforOAuthResult.Fail("Token request timed out after 30 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return InforOAuthResult.Fail($"Could not reach the token endpoint: {ex.Message}");
        }
        catch (Exception ex)
        {
            return InforOAuthResult.Fail(ex.Message);
        }
    }
}

/// <summary>Raw token-request outcome. <see cref="AccessToken"/> is set only on success.</summary>
public record InforOAuthResult(bool Success, string? AccessToken, int? ExpiresInSeconds, string Message)
{
    public static InforOAuthResult Ok(string token, int? expiresIn, string message) => new(true, token, expiresIn, message);
    public static InforOAuthResult Fail(string message) => new(false, null, null, message);
}
