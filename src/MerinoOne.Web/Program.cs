using MerinoOne.Web.Components;
using MerinoOne.Web.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

// Aspire shared defaults — service discovery, resilience, OTel, health checks.
// Required for the "https+http://supplierPortal-api" scheme to resolve when running under AppHost.
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();

builder.Services.AddScoped<TokenAccessor>();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
