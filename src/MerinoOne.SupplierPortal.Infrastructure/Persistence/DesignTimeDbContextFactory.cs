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
        var cfg = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "MerinoOne.SupplierPortal"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = cfg.GetConnectionString("DefaultConnection")
                  ?? "Data Source=10.10.104.12\\SqlExpress;Initial Catalog=merino-supplier-dev;User ID=sa;Password=sa@1234;TrustServerCertificate=True;Encrypt=True;";

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
