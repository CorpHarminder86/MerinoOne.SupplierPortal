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

        // Tenant/company foundation (TenantCompany module). PlatformSeeder + TenantSeeder run after the
        // base masters; ScopeBackfillSeeder then retro-tags existing rows. The heavy aggregate scope
        // stamping (and the Scope.FiltersEnabled flip) stays behind --backfill.
        var cs = cfg.GetConnectionString("DefaultConnection")!;

        logger?.LogInformation("Seed: PlatformSeeder");
        await PlatformSeeder.SeedAsync(ctx, cfg, ct);

        logger?.LogInformation("Seed: TenantSeeder");
        await TenantSeeder.SeedAsync(ctx, ct);

        // R4 (2026-06-26) — TSD R4 Addendum Component 5. Runs AFTER TenantSeeder so the tenant + tenant-admin
        // seccode (the owner of these tenant-wide config masters) exist. Seeds AttachmentEntity / AttachmentType
        // only — no AttachmentRequirementPolicy (absence = Optional).
        logger?.LogInformation("Seed: AttachmentGovernanceSeeder");
        await AttachmentGovernanceSeeder.SeedAsync(ctx, ct);

        // R5 (TSD R5 Addendum §4.1–4.2 / Component 1). Runs AFTER TenantSeeder so the tenant + tenant-admin seccode
        // exist. Seeds one admin.Company per tenant company (1:1 to tenantEntityId) + one ship-to CompanyAddress
        // carrying an erpCode, so inbound PO ship-to resolution (§6.2) has a code to resolve against.
        logger?.LogInformation("Seed: CompanySeeder");
        await CompanySeeder.SeedAsync(ctx, ct);

        // R5 (TSD R5 Addendum §4.7 / Component 7). Runs AFTER TenantSeeder so the tenant + tenant-admin seccode
        // exist. Seeds the ERP→portal PO-status mapping per tenant; the seeded default reproduces the R4 hardcoded
        // modified → Released behaviour, so out-of-the-box inbound-PO status derivation is unchanged.
        logger?.LogInformation("Seed: PoStatusMappingSeeder");
        await PoStatusMappingSeeder.SeedAsync(ctx, ct);

        // R8 (2026-07-04) — TSD R8 §4.4 / D6. Runs AFTER TenantSeeder. Seeds the per-tenant IDM transport endpoints
        // + default attachment-type mapping/gate rows (disabled by default) from the repo JSONata expressions;
        // hash-gated so a repo expression change flows to untouched rows but never clobbers a hand-edit.

        // R9 (2026-07-06) — TSD R9 §2.1. One OutboundIntegrationConfig row per tenant per transaction type (all 8) from
        // the repo LN expression catalogue. Every row seeds DispatchMode=Legacy — zero dispatch change until an
        // admin attests + flips to Dynamic. Per-slot hash-gated like the IDM seeder (hand-edits never clobbered).
        logger?.LogInformation("Seed: LnOutboundSeeder");
        await LnOutboundSeeder.SeedAsync(ctx, ct);

        // Light scope tagging (suppliers/masters/config/UserCompanyMaps + ensure flag OFF) runs BEFORE the
        // volume backfill so suppliers carry company 2000 when the heavy pass later joins on them.
        logger?.LogInformation("Seed: ScopeBackfillSeeder (light)");
        await ScopeBackfillSeeder.SeedLightAsync(ctx, ct);

        if (includeBackfill)
        {
            logger?.LogInformation("Seed: BackfillSeeder (large volume)");
            await BackfillSeeder.SeedAsync(ctx, cs, ct);

            // Heavy scope stamping runs AFTER the volume backfill so it covers the just-inserted aggregate
            // rows too, then flips Scope.FiltersEnabled ON (filters begin enforcing).
            logger?.LogInformation("Seed: ScopeBackfillSeeder (heavy aggregates + flag flip)");
            await ScopeBackfillSeeder.SeedHeavyAsync(ctx, cs, ct);
        }

        logger?.LogInformation("Seed: complete");
    }
}
