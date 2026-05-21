using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        // Mock integration services (Stage 1)
        services.AddScoped<INicValidationService, MockNicValidationService>();
        services.AddScoped<IDocumentValidationService, MockDocumentValidationService>();
        services.AddScoped<IInforIntegrationService, MockInforIntegrationService>();

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
