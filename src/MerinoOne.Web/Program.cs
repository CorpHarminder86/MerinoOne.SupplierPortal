using MerinoOne.Web.Components;
using MerinoOne.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Aspire shared defaults — service discovery, resilience, OTel, health checks.
// Required for the "https+http://supplierPortal-api" scheme to resolve when running under AppHost.
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();

// Data Protection — persist the key ring to a stable folder + fixed app name. Blazor's ProtectedLocalStorage
// encrypts the stored JWT with this provider; the framework default ring lives in the app-pool profile and is
// regenerated on every publish / recycle, which makes the stored token undecryptable and silently signs users
// out after each deploy. Persisting it (DataProtection:KeysPath) fixes that. Same app name + folder as the API.
var dp = builder.Services.AddDataProtection().SetApplicationName("MerinoOne.SupplierPortal");
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dpKeysPath))
{
    System.IO.Directory.CreateDirectory(dpKeysPath);
    dp.PersistKeysToFileSystem(new System.IO.DirectoryInfo(dpKeysPath));
}

builder.Services.AddScoped<TokenAccessor>();
builder.Services.AddScoped<CompanyState>();
builder.Services.AddScoped<ShellState>();
builder.Services.AddScoped<PageRefreshService>();
builder.Services.AddScoped<ApiErrorNotifier>();

// API base URL resolution order (first non-empty wins):
//   1. Env var SUPPLIERPORTAL_API_BASE_URL  - runtime override (containers, prod)
//   2. appsettings.{Env}.json -> SupplierPortal:ApiBaseUrl
//   3. appsettings.json       -> SupplierPortal:ApiBaseUrl
//   4. Legacy Api:BaseUrl key  - backwards-compatible
//   5. Aspire service-discovery default "https+http://supplierPortal-api"
//
// The "https+http://" scheme requires IHttpClientFactory + AddServiceDiscovery (wired via
// AddServiceDefaults above) and Aspire AppHost to be the launcher. Standalone runs should
// use a real http(s) URL via appsettings.Development.json or the env var.
var apiBaseUrl =
    builder.Configuration["SUPPLIERPORTAL_API_BASE_URL"]
    ?? builder.Configuration["SupplierPortal:ApiBaseUrl"]
    ?? builder.Configuration["Api:BaseUrl"];
var apiBaseAddress = string.IsNullOrWhiteSpace(apiBaseUrl)
    ? new Uri("https+http://supplierPortal-api")
    : new Uri(apiBaseUrl, UriKind.Absolute);

// Typed HttpClient via IHttpClientFactory so the service-discovery handler (configured by
// AddServiceDefaults / ConfigureHttpClientDefaults) sees and resolves the BaseAddress.
// Primary handler keeps the dev-cert relaxation that the old raw HttpClient had.
builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = apiBaseAddress;
    // 60s tolerates SMTP send (capped at 15s server-side) + slow first-cold-start API calls.
    // Real APIs that legitimately need longer should expose async/202-status patterns.
    client.Timeout = TimeSpan.FromSeconds(60);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
});

var app = builder.Build();

// Sub-path hosting (e.g. behind reverse proxy at /supplier-portal-dev/).
// Reads same AppBasePath setting that App.razor uses for <base href>.
// Strip trailing slash for UsePathBase ("/supplier-portal-dev"), keep it for <base href> ("/supplier-portal-dev/").
var rawBase = app.Configuration["AppBasePath"];
if (!string.IsNullOrWhiteSpace(rawBase) && rawBase != "/")
{
    var pathBase = "/" + rawBase.Trim('/');
    app.UsePathBase(pathBase);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

// File proxy — the Blazor app is served on a different host/port than the API, so a relative
// <img src="/api/document-uploads/{id}"> would 404 against the Web origin. These proxy routes
// hit the Web host (same origin as the page), grab the API response and stream the body straight
// back to the browser. Authentication: reads the JWT from a same-origin "merino-jwt" cookie
// that Login.razor sets after successful sign-in; the proxy attaches it as a bearer when calling
// the API. This bypasses the TokenAccessor scoping problem (Blazor circuit state isn't available
// from a fresh minimal-API HTTP scope).
//
// Anonymous /by-token variant exists for Register.razor previews before login — no cookie needed.
async Task<IResult> ProxyApiAsync(HttpContext http, IHttpClientFactory factory, string apiPath, bool requireAuth, CancellationToken ct,
    Guid? activeCompanyId = null)
{
    var apiBase = http.RequestServices.GetRequiredService<IConfiguration>()["SUPPLIERPORTAL_API_BASE_URL"]
                  ?? http.RequestServices.GetRequiredService<IConfiguration>()["SupplierPortal:ApiBaseUrl"]
                  ?? http.RequestServices.GetRequiredService<IConfiguration>()["Api:BaseUrl"]
                  ?? "https+http://supplierPortal-api";
    var client = factory.CreateClient(nameof(ApiClient));
    if (client.BaseAddress is null) client.BaseAddress = new Uri(apiBase, UriKind.Absolute);

    using var req = new HttpRequestMessage(HttpMethod.Get, apiPath);
    if (requireAuth)
    {
        var jwt = http.Request.Cookies["merino-jwt"];
        if (string.IsNullOrEmpty(jwt)) return Results.Unauthorized();
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
    }
    // Forward the caller's active company (same header ApiClient stamps on circuit-scoped calls). Without it a
    // multi-company principal's server-side company filter scopes to a default company and the entity 404s.
    // Optional — absent/empty preserves the pre-existing behaviour for callers that don't pass it.
    if (activeCompanyId is { } companyId && companyId != Guid.Empty)
        req.Headers.Add("X-Active-Company", companyId.ToString());

    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    if (!resp.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)resp.StatusCode);
    }
    var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var contentDisposition = resp.Content.Headers.ContentDisposition?.ToString();
    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

    // Forward Content-Disposition so download=foo.pdf works AND inline image rendering stays intact.
    if (!string.IsNullOrEmpty(contentDisposition))
    {
        http.Response.Headers["Content-Disposition"] = contentDisposition;
    }
    return Results.File(bytes, contentType);
}

app.MapGet("/files/proxy/{id:guid}", (Guid id, HttpContext http, IHttpClientFactory factory, CancellationToken ct)
    => ProxyApiAsync(http, factory, $"api/document-uploads/{id}", requireAuth: true, ct));

app.MapGet("/files/proxy/{id:guid}/by-token/{token}", (Guid id, string token, HttpContext http, IHttpClientFactory factory, CancellationToken ct)
    => ProxyApiAsync(http, factory, $"api/document-uploads/{id}/by-token/{token}", requireAuth: false, ct));

// R6 — invoice PDF (frozen snapshot). Same cookie-JWT → bearer proxy as /files/proxy: the API endpoint
// (GET api/invoices/{id}/pdf, policy Invoice.Read, seccode-scoped) returns application/pdf with a
// Content-Disposition filename ({invoiceNumber}.pdf) — both forwarded by ProxyApiAsync, so a plain
// <a download> on InvoiceDetail streams the file same-origin. The ?company=<guid> query param (stamped by
// InvoiceDetail from CompanyState) is forwarded as X-Active-Company so a multi-company principal's company
// filter resolves the invoice's company instead of 404ing; absent/invalid ⇒ header omitted (legacy behaviour).
app.MapGet("/files/invoice-pdf/{id:guid}", (Guid id, HttpContext http, IHttpClientFactory factory, CancellationToken ct)
    => ProxyApiAsync(http, factory, $"api/invoices/{id}/pdf", requireAuth: true, ct,
        activeCompanyId: Guid.TryParse(http.Request.Query["company"], out var companyId) ? companyId : null));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
