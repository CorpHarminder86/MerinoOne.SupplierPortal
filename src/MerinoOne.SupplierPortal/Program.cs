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
    });

builder.Services.AddAuthorization();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, MerinoOne.SupplierPortal.Identity.PermissionPolicyProvider>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, MerinoOne.SupplierPortal.Identity.PermissionRequirementHandler>();

var app = builder.Build();

// CLI seed mode: `dotnet run -- seed` or `seed --backfill`
if (args.Length > 0 && args[0].Equals("seed", StringComparison.OrdinalIgnoreCase))
{
    using (var scope = app.Services.CreateScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ctx.Database.MigrateAsync();
    }
    var withBackfill = args.Any(a => a.Equals("--backfill", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"Seeding (backfill={withBackfill})...");
    await SeedRunner.RunAsync(app.Services, withBackfill);
    Console.WriteLine("Seed complete.");
    return;
}

// Migrate-only mode for normal startup; seeding is opt-in
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
app.UseAuthorization();
app.MapControllers();

app.Run();
