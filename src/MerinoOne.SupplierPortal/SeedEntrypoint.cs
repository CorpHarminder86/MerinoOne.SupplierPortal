using MerinoOne.SupplierPortal.Application;
using MerinoOne.SupplierPortal.Infrastructure;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal;

/// <summary>
/// CLI seed entrypoint. Runs the seeders through a MINIMAL DI container (config + Application +
/// Infrastructure + plain console logging) — deliberately NOT the full <c>WebApplication</c> host.
/// Building the web host (Serilog file sinks + Kestrel/host integration) hangs under a non-interactive
/// redirected-stdout context; this path mirrors what <c>dotnet ef</c> does and is reliable for CLI runs.
/// The Infrastructure fallback principals (system/anonymous) bypass the tenant + company filters, which
/// is exactly what seeding needs. Progress is written to stdout and flushed after every step.
/// </summary>
public static class SeedEntrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        var withBackfill = args.Any(a => a.Equals("--backfill", StringComparison.OrdinalIgnoreCase));
        Log($"seed starting (backfill={withBackfill})");

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        if (string.IsNullOrWhiteSpace(config.GetConnectionString("DefaultConnection")))
        {
            Log("ERROR: ConnectionStrings:DefaultConnection not found (appsettings.json or ConnectionStrings__DefaultConnection env var).");
            return 2;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        services.AddApplication();
        services.AddInfrastructure(config);

        await using var sp = services.BuildServiceProvider();
        Log("DI container built");

        await using (var scope = sp.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Log("applying migrations...");
            await ctx.Database.MigrateAsync();
            Log("migrations up to date");
        }

        Log("running seeders...");
        await SeedRunner.RunAsync(sp, withBackfill);
        Log("seed complete.");
        return 0;
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"[seed] {msg}");
        Console.Out.Flush();
    }
}
