using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using MerinoOne.SupplierPortal.Application.SystemSettings.Registry;
using MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;
using MerinoOne.SupplierPortal.Infrastructure.Security;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("DefaultConnection")
                  ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");

        services.AddScoped<AuditableEntityInterceptor>();

        services.AddDbContext<AppDbContext>((sp, opts) =>
        {
            opts.UseSqlServer(cs, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name));
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // Dapper-backed raw SQL factory (global search, audit trail)
        services.AddSingleton<ISqlConnectionFactory>(_ => new SqlConnectionFactory(cs));

        // Mock integration services (Stage 1)
        services.AddScoped<INicValidationService, MockNicValidationService>();
        services.AddScoped<IDocumentValidationService, MockDocumentValidationService>();
        services.AddScoped<IInforIntegrationService, MockInforIntegrationService>();

        // In-process memory cache — backs the IEmailTemplateRenderer 60s lookup. Safe to call
        // twice; Microsoft.Extensions registers a single MemoryCache + IMemoryCache pair.
        services.AddMemoryCache();

        // Admin-editable email template renderer (used by the TemplateAwareEmailService
        // decorator below, and by the test-send command in the EmailTemplates admin endpoints).
        services.AddScoped<IEmailTemplateRenderer, EmailTemplateRenderer>();

        // Email transport. Defaults to Live SMTP — opt into Mock by setting
        // Email:Mode = "Mock" in appsettings.Development.json or env var. The chosen sender is
        // wrapped by TemplateAwareEmailService so admin-edited templates win over hardcoded
        // bodies; when no active template exists, the decorator forwards to the inner service.
        var emailMode = cfg["Email:Mode"] ?? "Live";
        if (emailMode.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<MockEmailService>();
            services.AddScoped<IEmailService>(sp => new TemplateAwareEmailService(
                sp.GetRequiredService<MockEmailService>(),
                sp.GetRequiredService<IEmailTemplateRenderer>(),
                sp.GetRequiredService<ILogger<TemplateAwareEmailService>>()));
        }
        else
        {
            services.AddScoped<SmtpEmailService>();
            services.AddScoped<IEmailService>(sp => new TemplateAwareEmailService(
                sp.GetRequiredService<SmtpEmailService>(),
                sp.GetRequiredService<IEmailTemplateRenderer>(),
                sp.GetRequiredService<ILogger<TemplateAwareEmailService>>()));
        }

        // Cryptographically-strong numeric OTP generator (invite + MFA flows).
        services.AddSingleton<IOtpCodeGenerator, OtpCodeGenerator>();

        // Password hashing — adapter over the static PasswordHasher (Application has no Infra ref).
        services.AddSingleton<IPasswordHasher, PasswordHasherService>();

        // System settings — generic shell, per-category seeds, password protection, cached readers.
        services.AddDataProtection();
        // Singleton — DataProtectorSettingProtector wraps the singleton IDataProtectionProvider
        // and is consumed from singleton EmailConfigService and scoped MediatR handlers alike.
        services.AddSingleton<ISettingProtector, DataProtectorSettingProtector>();

        // Seeds: registered concretely so services can ctor-inject the typed seed and also as
        // ISettingsCategorySeed so SettingsSeedRegistry enumerates them.
        services.AddSingleton<EmailConfigSeed>();
        services.AddSingleton<SupplierInviteSeed>();
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<EmailConfigSeed>());
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<SupplierInviteSeed>());
        services.AddSingleton<SettingsSeedRegistry>();

        // Cached readers — singleton so the cache persists across requests; expose both the
        // typed reader and ISettingsCacheInvalidator so Save/Reset handlers fan invalidations out.
        services.AddSingleton<EmailConfigService>();
        services.AddSingleton<IEmailConfig>(sp => sp.GetRequiredService<EmailConfigService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<EmailConfigService>());

        services.AddSingleton<SupplierInviteSettingsService>();
        services.AddSingleton<ISupplierInviteSettings>(sp => sp.GetRequiredService<SupplierInviteSettingsService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<SupplierInviteSettingsService>());

        // ef tooling / migrations / seed contexts get the anonymous user
        services.TryAddCurrentUserFallback();

        return services;
    }

    private static IServiceCollection TryAddCurrentUserFallback(this IServiceCollection services)
    {
        // host project (API/Web) will register the real HttpContext-backed implementation;
        // here we provide a fallback so design-time tools (dotnet ef) and tests don't crash.
        if (!services.Any(d => d.ServiceType == typeof(ICurrentUser)))
        {
            services.AddScoped<ICurrentUser, AnonymousCurrentUser>();
        }
        return services;
    }
}
