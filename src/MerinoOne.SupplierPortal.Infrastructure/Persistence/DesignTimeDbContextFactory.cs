using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // SECURITY: no plaintext SA secret in source. ef tooling resolves the dev connection string from the same
        // user-secrets store as the API host (UserSecretsId pinned below — the GUID is not a secret), or from the
        // ConnectionStrings__DefaultConnection environment variable. Fails fast if neither is set.
        var cfg = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "MerinoOne.SupplierPortal"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets("65eef0aa-77fd-459b-9e4f-39830a3cb793")
            .AddEnvironmentVariables()
            .Build();

        var cs = cfg.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured for design-time tooling. Set it via " +
                "user-secrets (dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"...\" --project " +
                "src/MerinoOne.SupplierPortal) or the ConnectionStrings__DefaultConnection environment variable.");

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name))
            .Options;

        var user = new AnonymousCurrentUser();
        var company = new AnonymousCurrentCompany();
        return new AppDbContext(
            opts,
            user,
            company,
            new AuditableEntityInterceptor(user),
            new ScopeStampInterceptor(user, company));
    }
}
