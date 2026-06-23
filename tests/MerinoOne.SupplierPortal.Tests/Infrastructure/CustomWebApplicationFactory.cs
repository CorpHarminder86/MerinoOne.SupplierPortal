using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// Boots the REAL API host (<c>WebApplicationFactory&lt;Program&gt;</c>) against a DEDICATED SQL-Express test
/// database, with <c>Integration:Mode=Mock</c> so the outbox dispatcher's ERP call resolves deterministically.
///
/// <para>The environment is forced to <c>Development</c> so the app's own startup <c>MigrateAsync</c> runs and
/// stands the schema up on a fresh DB. The migrations' <c>.Designer.cs</c> are gitignored but present on disk
/// locally, so EF can apply them here.</para>
///
/// <para>The OutboxDispatcherWorker (and the email worker) are deliberately LEFT RUNNING — the invoice
/// auto-post regression test asserts on the dispatcher's effects (the outbox row landing + the outbound
/// InforSyncLog), which is exactly the EF/SQL boundary a pure unit test can't reach.</para>
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Dedicated test DB on the existing SQL Express. SEPARATE from -dev so a test run never touches dev data.
    /// </summary>
    public const string TestConnectionString =
        "Data Source=10.10.104.12\\SqlExpress;Initial Catalog=merino-supplier-test;User ID=sa;Password=sa@1234;Encrypt=True;TrustServerCertificate=True;";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development → the startup MigrateAsync runs (see Program.cs: app.Environment.IsDevelopment()).
        builder.UseEnvironment("Development");

        // UseSetting writes into the HOST builder's configuration, which the app reads via
        // builder.Configuration at service-registration time (AddInfrastructure(builder.Configuration)).
        // A ConfigureAppConfiguration in-memory source added here is appended AFTER appsettings.json but is
        // NOT guaranteed to win for values read during the host-builder phase — UseSetting is. This is the
        // reason the dedicated test DB MUST be wired via UseSetting (otherwise tests silently fall back to the
        // appsettings.json -dev connection string).
        builder.UseSetting("ConnectionStrings:DefaultConnection", TestConnectionString);
        // Deterministic mock ERP dispatch for the outbox → dispatcher → Mock path.
        builder.UseSetting("Integration:Mode", "Mock");
        // Don't try to send real email during tests.
        builder.UseSetting("Email:Mode", "Mock");
        // Keep the Data Protection key ring out of the dev folder; fall back to the default ring.
        builder.UseSetting("DataProtection:KeysPath", "");
        // Keep Scalar off to shave a little startup work (the protected endpoints are what we test).
        builder.UseSetting("Scalar:Enabled", "false");

        // Belt-and-suspenders: also add the in-memory source LAST so anything reading IConfiguration after the
        // host is built (handlers, the dispatcher scope) sees the same values.
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TestConnectionString,
                ["Integration:Mode"] = "Mock",
                ["Email:Mode"] = "Mock",
                ["DataProtection:KeysPath"] = "",
                ["Scalar:Enabled"] = "false",
            });
        });
    }
}
