using System.Net.Http.Headers;
using System.Net.Http.Json;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Contracts.Common;

namespace MerinoOne.Web.Services;

/// <summary>
/// Wraps HttpClient + bearer token plumbing for the Blazor frontend.
///
/// Error convention:
///   - LoginAsync allows ApiException to surface (the Login page wants the typed exception).
///   - Every other method ALWAYS returns an ApiResult envelope. Backend non-2xx, network
///     failures, JSON parse errors, and session expiry are folded into Success=false +
///     Errors[...] so page-level @onclick handlers can stay simple and never raise an
///     unhandled exception that would break the Blazor circuit. A 401 still fires
///     TokenAccessor.NotifySessionExpired() so the layout's sign-out chain runs.
/// </summary>
public class ApiClient
{
    private const string ActiveCompanyHeader = "X-Active-Company";

    private readonly HttpClient _http;
    private readonly TokenAccessor _token;
    private readonly CompanyState _company;

    public ApiClient(HttpClient http, TokenAccessor token, CompanyState company)
    {
        _http = http;
        _token = token;
        _company = company;
    }

    /// <summary>
    /// Stamp the per-circuit auth + company-scope headers onto the shared typed HttpClient before each
    /// send. Bearer comes from the scoped <see cref="TokenAccessor"/>; the active-company id from the
    /// scoped <see cref="CompanyState"/>. Both are scoped state that a DelegatingHandler cannot reach
    /// (see CompanyState remarks), so we set them here on DefaultRequestHeaders — the established pattern.
    /// </summary>
    internal void EnsureAuth()
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(_token.Token)
            ? null
            : new AuthenticationHeaderValue("Bearer", _token.Token);

        // Always replace (never accumulate) — DefaultRequestHeaders persists across the shared client.
        _http.DefaultRequestHeaders.Remove(ActiveCompanyHeader);
        if (_company.ActiveCompanyId is { } companyId)
            _http.DefaultRequestHeaders.Add(ActiveCompanyHeader, companyId.ToString());
    }

    public async Task<ApiResult<LoginResponse>?> LoginAsync(LoginRequest req)
    {
        HttpResponseMessage resp;
        try { resp = await _http.PostAsJsonAsync("api/auth/login", req); }
        catch (HttpRequestException ex) { throw new ApiException(0, "Cannot reach server", new[] { ex.Message }, null); }

        var body = await resp.Content.ReadFromJsonAsync<ApiResult<LoginResponse>>();
        if (body?.Success == true && body.Data != null && !body.Data.RequiresMfa && !string.IsNullOrEmpty(body.Data.Token))
        {
            _token.SetSession(
                token: body.Data.Token,
                userCode: body.Data.UserCode,
                email: req.Email.Contains('@') ? req.Email : null,
                fullName: body.Data.FullName,
                roles: body.Data.Roles,
                permissions: body.Data.Permissions,
                expiresAt: body.Data.ExpiresAt,
                mustChangePassword: body.Data.MustChangePassword);
        }
        return body;
    }

    public Task<ApiResult<T>?> GetAsync<T>(string url) =>
        RunAsync(() => _http.GetAsync(url),
                 resp => ReadEnvelopeAsync<ApiResult<T>>(resp),
                 errors => new ApiResult<T> { Success = false, Errors = errors });

    public Task<ApiResult<T>?> PostAsync<T, TBody>(string url, TBody body) =>
        RunAsync(() => _http.PostAsJsonAsync(url, body),
                 resp => ReadEnvelopeAsync<ApiResult<T>>(resp),
                 errors => new ApiResult<T> { Success = false, Errors = errors });

    public Task<ApiResult?> PostAsync<TBody>(string url, TBody body) =>
        RunAsync(() => _http.PostAsJsonAsync(url, body),
                 resp => ReadEnvelopeAsync<ApiResult>(resp),
                 errors => new ApiResult { Success = false, Errors = errors });

    public Task<ApiResult<T>?> PutAsync<T, TBody>(string url, TBody body) =>
        RunAsync(() => _http.PutAsJsonAsync(url, body),
                 resp => ReadEnvelopeAsync<ApiResult<T>>(resp),
                 errors => new ApiResult<T> { Success = false, Errors = errors });

    public Task<ApiResult?> PutAsync<TBody>(string url, TBody body) =>
        RunAsync(() => _http.PutAsJsonAsync(url, body),
                 resp => ReadEnvelopeAsync<ApiResult>(resp),
                 errors => new ApiResult { Success = false, Errors = errors });

    public Task<ApiResult?> DeleteAsync(string url) =>
        RunAsync(() => _http.DeleteAsync(url),
                 resp => ReadEnvelopeAsync<ApiResult>(resp),
                 errors => new ApiResult { Success = false, Errors = errors });

    /// <summary>
    /// Multipart POST for file uploads. Caller builds the <see cref="MultipartFormDataContent"/>
    /// — including any non-file form fields — and we surface it through the same RunAsync
    /// failure-folding so anonymous upload endpoints behave like every other ApiClient call.
    /// </summary>
    public Task<ApiResult<T>?> PostMultipartAsync<T>(string url, MultipartFormDataContent content) =>
        RunAsync(() => _http.PostAsync(url, content),
                 resp => ReadEnvelopeAsync<ApiResult<T>>(resp),
                 errors => new ApiResult<T> { Success = false, Errors = errors });

    /// <summary>
    /// Streaming GET — returns the raw <see cref="HttpResponseMessage"/> so the caller (e.g.
    /// the <c>/files/proxy/{id}</c> minimal-API endpoint in MerinoOne.Web) can pipe the response
    /// body straight back to the browser without buffering the whole file. Caller is responsible
    /// for disposing the response. Does NOT fold failures into an envelope — this is a transport
    /// helper for proxying, not an API result wrapper.
    /// </summary>
    public Task<HttpResponseMessage> GetRawAsync(string url, CancellationToken ct = default)
    {
        EnsureAuth();
        return _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// GET a non-enveloped JSON body straight into <typeparamref name="T"/> (for diagnostic endpoints
    /// like <c>/api/auth/whoami</c> that return a raw anonymous object rather than the Result&lt;T&gt;
    /// envelope). Returns null on any failure — never throws.
    /// </summary>
    public async Task<T?> GetRawJsonAsync<T>(string url) where T : class
    {
        EnsureAuth();
        try
        {
            using var resp = await _http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) { _token.NotifySessionExpired(); return null; }
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength == 0) return null;
            return await resp.Content.ReadFromJsonAsync<T>(new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    public Task LogoutAsync()
    {
        EnsureAuth();
        try { return _http.PostAsync("api/auth/logout", content: null); }
        catch { return Task.CompletedTask; }
    }

    private async Task<TResult?> RunAsync<TResult>(
        Func<Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, Task<TResult?>> read,
        Func<List<string>, TResult> failFactory)
        where TResult : class
    {
        EnsureAuth();
        HttpResponseMessage? resp = null;
        try
        {
            resp = await send();
            return await read(resp);
        }
        catch (ApiException ex)
        {
            // 401 → fire session expiry so layout can sign the user out + toast once.
            if (ex.StatusCode == 401) _token.NotifySessionExpired();
            var errors = ex.Errors.Length > 0 ? ex.Errors.ToList() : new List<string> { ex.Title };
            return failFactory(errors);
        }
        catch (HttpRequestException ex)
        {
            return failFactory(new List<string> { "Cannot reach server. " + ex.Message });
        }
        catch (TaskCanceledException ex)
        {
            return failFactory(new List<string> { "Request timed out. " + ex.Message });
        }
        catch (Exception ex)
        {
            return failFactory(new List<string> { ex.Message });
        }
        finally { resp?.Dispose(); }
    }

    private static async Task<T?> ReadEnvelopeAsync<T>(HttpResponseMessage resp) where T : class
    {
        await resp.EnsureSuccessOrThrowAsync();
        if (resp.Content.Headers.ContentLength == 0) return null;
        try { return await resp.Content.ReadFromJsonAsync<T>(); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
