using System.Net.Http.Headers;
using System.Net.Http.Json;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Contracts.Common;

namespace MerinoOne.Web.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly TokenAccessor _token;

    public ApiClient(HttpClient http, TokenAccessor token)
    {
        _http = http;
        _token = token;
    }

    private void EnsureAuth()
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(_token.Token)
            ? null
            : new AuthenticationHeaderValue("Bearer", _token.Token);
    }

    public async Task<ApiResult<LoginResponse>?> LoginAsync(LoginRequest req)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/login", req);
        var body = await resp.Content.ReadFromJsonAsync<ApiResult<LoginResponse>>();
        if (body?.Success == true && body.Data != null)
        {
            _token.Token = body.Data.Token;
            _token.UserCode = body.Data.UserCode;
            _token.FullName = body.Data.FullName;
            _token.Roles = body.Data.Roles;
            _token.Permissions = body.Data.Permissions;
            _token.ExpiresAt = body.Data.ExpiresAt;
        }
        return body;
    }

    public async Task<ApiResult<T>?> GetAsync<T>(string url)
    {
        EnsureAuth();
        var resp = await _http.GetAsync(url);
        return await resp.Content.ReadFromJsonAsync<ApiResult<T>>();
    }

    public async Task<ApiResult<T>?> PostAsync<T, TBody>(string url, TBody body)
    {
        EnsureAuth();
        var resp = await _http.PostAsJsonAsync(url, body);
        return await resp.Content.ReadFromJsonAsync<ApiResult<T>>();
    }

    public async Task<ApiResult?> PostAsync<TBody>(string url, TBody body)
    {
        EnsureAuth();
        var resp = await _http.PostAsJsonAsync(url, body);
        return await resp.Content.ReadFromJsonAsync<ApiResult>();
    }
}
