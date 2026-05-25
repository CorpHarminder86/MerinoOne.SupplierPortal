using MerinoOne.Web.Components;
using MerinoOne.Web.Services;
using Radzen;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();

builder.Services.AddScoped<TokenAccessor>();
builder.Services.AddScoped<ShellState>();
builder.Services.AddScoped<PageRefreshService>();
builder.Services.AddScoped<ApiErrorNotifier>();
builder.Services.AddScoped(sp =>
{
    var token = sp.GetRequiredService<TokenAccessor>();
    var http = new HttpClient(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    {
        BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "https://localhost:7270/")
    };
    return new ApiClient(http, token);
});

var app = builder.Build();

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
