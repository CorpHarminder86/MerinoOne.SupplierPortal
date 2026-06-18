using System.Text;
using MerinoOne.SupplierPortal.Application;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Identity;
using MerinoOne.SupplierPortal.Infrastructure;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using MerinoOne.SupplierPortal.Middlewares;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// CLI seed mode runs through a minimal, web-host-free entrypoint (see SeedEntrypoint) — building the
// full WebApplication host (Serilog file sinks + host integration) hangs under non-interactive CLI runs.
if (args.Length > 0 && args[0].Equals("seed", StringComparison.OrdinalIgnoreCase))
{
    return await MerinoOne.SupplierPortal.SeedEntrypoint.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Structured logging — console + per-level rolling daily file sinks (TSD §11 hardening).
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/info-.log",
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Information,
        shared: true)
    .WriteTo.File(
        path: "logs/error-.log",
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Error,
        shared: true)
    .WriteTo.File(
        path: "logs/fatal-.log",
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Fatal,
        shared: true));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(options =>
{
    // Inject a Bearer security scheme into the OpenAPI doc so Scalar surfaces a JWT
    // "Authorize" input. Without this transformer the doc has no security definitions
    // and the Scalar auth panel renders empty.
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Description = "JWT obtained from POST /api/auth/login. Paste the raw token (no 'Bearer ' prefix)."
        };
        // X-APIKey scheme for the inbound integration endpoints (Infor LN). Mirrors the Bearer
        // definition above so Scalar surfaces an apiKey input. Inbound endpoints opt into this scheme
        // via [Authorize(AuthenticationSchemes="ApiKey", …)].
        document.Components.SecuritySchemes["ApiKey"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            Name = "X-APIKey",
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Description = "Inbound integration key (X-APIKey header). Format: mok_<base64url>. Issued via POST /api/admin/api-keys; shown once."
        };

        document.Security ??= new List<Microsoft.OpenApi.OpenApiSecurityRequirement>();
        document.Security.Add(new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>()
        });
        return Task.CompletedTask;
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
// Active-company context for the always-on company filter. Registered before AddInfrastructure so the
// system-company fallback inside AddInfrastructure does not override the HttpContext-backed impl.
builder.Services.AddScoped<ICurrentCompany, HttpContextCurrentCompany>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Permissive CORS for Blazor Web App in dev (tighten in prod)
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["SigningKey"]!))
        };
    })
    // Named, non-default "ApiKey" scheme for inbound integration (Infor LN). JWT stays the default
    // scheme; inbound endpoints opt in via [Authorize(AuthenticationSchemes="ApiKey", Policy="Integration.Inbound.*")].
    .AddScheme<MerinoOne.SupplierPortal.Identity.ApiKeyAuth.ApiKeyAuthenticationOptions,
               MerinoOne.SupplierPortal.Identity.ApiKeyAuth.ApiKeyAuthenticationHandler>(
        MerinoOne.SupplierPortal.Identity.ApiKeyAuth.ApiKeyAuthenticationOptions.SchemeName, _ => { });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, MerinoOne.SupplierPortal.Identity.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MerinoOne.SupplierPortal.Identity.PermissionRequirementHandler>();

// Rate limiting for inbound integration. Partitioned on the X-APIKey prefix (fallback to remote IP).
// The named "inbound" policy is applied to the inbound integration route group by backend-developer.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("inbound", httpContext =>
    {
        var apiKey = httpContext.Request.Headers["X-APIKey"].FirstOrDefault();
        var partitionKey = !string.IsNullOrEmpty(apiKey)
            ? "key:" + (apiKey.Length >= 8 ? apiKey[..8] : apiKey)        // prefix only — never the full secret
            : "ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });

    // Anonymous geo typeahead for onboarding. Partitioned on the invite-token prefix + remote IP.
    options.AddPolicy("public-geo", httpContext =>
    {
        var token = httpContext.Request.Query["token"].FirstOrDefault();
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var partitionKey = (string.IsNullOrEmpty(token)
            ? "ip:" + ip
            : "tok:" + (token.Length >= 8 ? token[..8] : token)) + "|" + ip;

        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

// Migrate-only mode for normal startup; seeding is opt-in (see SeedEntrypoint for `seed` CLI mode)
if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ctx.Database.MigrateAsync();
    }
}

// OpenAPI + Scalar exposed in every environment — protected endpoints still require a
// valid JWT, so the docs UI is safe to publish. Disable by setting Scalar:Enabled = false
// in appsettings.Production.json if you need to hide the API surface.
if (app.Configuration.GetValue("Scalar:Enabled", true))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "MerinoOne Supplier Portal API";
        options.AddPreferredSecuritySchemes("Bearer");
        options.PersistentAuthentication = true;
        options.DefaultHttpClient = new(ScalarTarget.Shell, ScalarClient.Curl);
        options.HideClientButton = false;
    });
}

app.UseMiddleware<GlobalExceptionHandler>();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();

return 0;
