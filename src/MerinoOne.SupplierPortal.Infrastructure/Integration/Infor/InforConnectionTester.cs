using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Drives the Settings "Test connection" button: requests an OAuth2 token via the shared
/// <see cref="InforOAuthTokenClient"/> and maps the raw outcome to the Application-facing
/// <see cref="InforTokenResult"/> (the access token is intentionally dropped — the test only needs
/// to know the request succeeded and how long the token would live).
///
/// Runs server-side so there is no CORS hop and no browser-visible API key — the Outlook plugin
/// needed a reverse-proxy controller purely to dodge those browser constraints, which do not apply here.
/// </summary>
public class InforConnectionTester : IInforConnectionTester
{
    private readonly InforOAuthTokenClient _oauth;

    public InforConnectionTester(InforOAuthTokenClient oauth) => _oauth = oauth;

    public async Task<InforTokenResult> RequestTokenAsync(InforTokenRequest request, CancellationToken ct = default)
    {
        var result = await _oauth.RequestAsync(
            request.AccessTokenUrl, request.ClientId, request.ClientSecret, request.Username, request.Password, ct);
        return new InforTokenResult(result.Success, result.Message, result.ExpiresInSeconds);
    }
}
