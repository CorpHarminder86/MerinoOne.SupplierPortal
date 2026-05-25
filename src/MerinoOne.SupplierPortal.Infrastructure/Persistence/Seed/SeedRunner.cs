using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class SeedRunner
{
    public static async Task RunAsync(IServiceProvider sp, bool includeBackfill, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SeedRunner");

        logger?.LogInformation("Seed: PermissionSeeder");
        await PermissionSeeder.SeedAsync(ctx, ct);

        logger?.LogInformation("Seed: UserSeeder");
        await UserSeeder.SeedAsync(ctx, ct);

        logger?.LogInformation("Seed: SupplierSeeder");
        await SupplierSeeder.SeedAsync(ctx, ct);

        logger?.LogInformation("Seed: MasterSeeder");
        await MasterSeeder.SeedAsync(ctx, ct);

        logger?.LogInformation("Seed: EmailTemplateSeeder");
        await EmailTemplateSeeder.SeedAsync(ctx, ct);

        if (includeBackfill)
        {
            logger?.LogInformation("Seed: BackfillSeeder (large volume)");
            var cs = cfg.GetConnectionString("DefaultConnection")!;
            await BackfillSeeder.SeedAsync(ctx, cs, ct);
        }

        logger?.LogInformation("Seed: complete");
    }
}
