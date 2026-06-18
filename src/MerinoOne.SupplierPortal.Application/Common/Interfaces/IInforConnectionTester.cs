namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Performs the live OAuth2 token request against Infor Mingle SSO. Lives behind this interface so the
/// Application-layer command handler stays free of HTTP concerns (clean architecture I/O boundary); the
/// concrete implementation lives in Infrastructure.
/// </summary>
public interface IInforConnectionTester
{
    Task<InforTokenResult> RequestTokenAsync(InforTokenRequest request, CancellationToken ct = default);
}

/// <summary>Resolved (plaintext) credentials for a single token request.</summary>
public record InforTokenRequest(
    string AccessTokenUrl,
    string ClientId,
    string ClientSecret,
    string Username,
    string Password);

/// <summary>Outcome of the token request. <see cref="ExpiresInSeconds"/> is set on success.</summary>
public record InforTokenResult(bool Success, string Message, int? ExpiresInSeconds);
