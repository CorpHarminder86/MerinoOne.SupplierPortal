using Dapper;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// Retro-tags existing rows with the new tenant + company scope so the always-on filters (once flipped on
/// via <c>Scope.FiltersEnabled</c>) don't render legacy NULL-scope data invisible.
///
/// Split into a LIGHT pass (always) and a HEAVY pass (behind <c>--backfill</c>):
///   LIGHT (small / config / masters):
///     - <c>TenantId = Merino</c> on every tenant-scoped config + master row + user that lacks it.
///     - legacy <c>PaymentTerm</c> / <c>DeliveryTerm</c> / <c>Supplier</c> → company <c>2000</c>.
///     - populate <c>UserCompanyMap</c> (supplier users → their mapped supplier's company; one isDefault).
///       Tenant Admin needs none (implicit-all).
///   HEAVY (big proc aggregates, set-based <c>UPDATE…FROM</c> raw SQL via the supplier/PO joins):
///     - PurchaseOrder/Asn/Invoice/Payment/SupplierVerification (direct supplierId), CreditDebitNote
///       (via Invoice), DeliverySchedule/CommunicationMessage (via PurchaseOrder), GoodsReceipt (via PO line),
///       DocumentUpload (via owner supplier). No client round-trip; one statement per table.
///
/// Idempotent: every UPDATE is predicated on <c>tenantId IS NULL</c> (or tenantEntityId IS NULL), and the
/// UserCompanyMap insert is a NOT-EXISTS. Re-runnable. Uses the same raw-SQL/connection approach as
/// <see cref="BackfillSeeder"/>.
/// </summary>
public static class ScopeBackfillSeeder
{
    public const string FlagCategory = "Scope";
    public const string FlagKey = "FiltersEnabled";

    /// <summary>
    /// LIGHT pass — always runs. Ensures the rollout flag exists (OFF), tenant-tags config + users, tags
    /// legacy masters + suppliers to company 2000, and populates UserCompanyMap for supplier users. Runs
    /// BEFORE the volume BackfillSeeder so suppliers carry company 2000 when the heavy pass later joins on them.
    /// </summary>
    public static async Task SeedLightAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var tenantId = TenantSeeder.TenantId;
        var company2000 = TenantSeeder.CompanyId("2000");

        // Ensure the rollout flag row exists (default OFF). The compiled scope filters OR against this gate
        // (AppDbContext.ScopeFiltersEnabled) so NULL-scope rows stay visible until it is flipped ON.
        await EnsureScopeFlagAsync(ctx, ct);

        await TagTenantScopedConfigAsync(ctx, tenantId, ct);
        await TagMastersAndSuppliersAsync(ctx, tenantId, company2000, ct);
        await PopulateUserCompanyMapsAsync(ctx, tenantId, DateTime.UtcNow, ct);
    }

    /// <summary>
    /// HEAVY pass — behind <c>--backfill</c>. Runs AFTER the volume BackfillSeeder so it stamps the
    /// just-inserted bulk aggregate rows too. Set-based UPDATE…FROM via supplier/PO joins, then flips
    /// Scope.FiltersEnabled ON — the moment the always-on tenant + company filters begin enforcing.
    /// </summary>
    public static async Task SeedHeavyAsync(AppDbContext ctx, string connectionString, CancellationToken ct = default)
    {
        await StampAggregateScopeAsync(connectionString, TenantSeeder.TenantId, ct);

        // Only after every legacy row is stamped do we flip the gate ON. Flipping it here (rather than wiring
        // uncertain logic into the compiled filter) is the SAFE rollout: a normal `seed` run leaves the flag
        // untouched, so an operator deliberately runs `seed --backfill` to go live.
        await SetScopeFlagAsync(ctx, true, ct);
        Console.WriteLine("[scope-backfill] Scope.FiltersEnabled flipped ON — tenant + company filters now enforce.");
    }

    /// <summary>Idempotently insert the Scope.FiltersEnabled flag (default "false") if it doesn't exist.</summary>
    public static async Task EnsureScopeFlagAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var exists = await ctx.SystemSettings.IgnoreQueryFilters()
            .AnyAsync(s => s.Category == FlagCategory && s.SettingKey == FlagKey, ct);
        if (exists) return;

        ctx.SystemSettings.Add(new SystemSetting
        {
            Id = DeterministicId.From("SystemSetting", $"{FlagCategory}|{FlagKey}"),
            Category = FlagCategory,
            SettingKey = FlagKey,
            SettingValue = "false",
            Description = "Master switch for the always-on tenant + company scope filters. Flipped ON only after the scope backfill stamps every legacy row (prevents a dark portal during the backfill window).",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>Set the Scope.FiltersEnabled flag. Creates the row if absent.</summary>
    public static async Task SetScopeFlagAsync(AppDbContext ctx, bool enabled, CancellationToken ct = default)
    {
        await EnsureScopeFlagAsync(ctx, ct);
        var value = enabled ? "true" : "false";
        await ctx.SystemSettings.IgnoreQueryFilters()
            .Where(s => s.Category == FlagCategory && s.SettingKey == FlagKey)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SettingValue, value)
                .SetProperty(x => x.UpdatedBy, "seed")
                .SetProperty(x => x.UpdatedOn, DateTime.UtcNow), ct);
    }

    /// <summary>Tenant-tag every tenant-scoped CONFIG / integration row + every user that lacks a tenant.</summary>
    private static async Task TagTenantScopedConfigAsync(AppDbContext ctx, Guid tenantId, CancellationToken ct)
    {
        // AppUser: only tag users that have no tenant AND are NOT the cross-tenant Platform Admin.
        await ctx.AppUsers.IgnoreQueryFilters()
            .Where(u => u.TenantId == null && u.UserCode != PlatformSeeder.UserCode)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TenantId, tenantId), ct);

        await ctx.Roles.IgnoreQueryFilters()
            .Where(r => r.TenantId == null && r.Name != "PlatformAdmin")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TenantId, tenantId), ct);

        await ctx.SupplierInvites.IgnoreQueryFilters()
            .Where(i => i.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.TenantId, tenantId), ct);

        // EmailTemplate is per-tenant unique on (tenantId, templateKey). TenantSeeder has already created
        // Merino's own template set, so the legacy global (null-tenant) rows would collide if retagged to
        // Merino for a key the tenant already owns. Soft-delete those duplicate legacy globals; retag the
        // remainder (keys Merino does NOT yet own) to Merino.
        var merinoKeys = await ctx.EmailTemplates.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId && !t.IsDeleted)
            .Select(t => t.TemplateKey)
            .ToListAsync(ct);

        await ctx.EmailTemplates.IgnoreQueryFilters()
            .Where(t => t.TenantId == null && merinoKeys.Contains(t.TemplateKey))
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsDeleted, true)
                .SetProperty(t => t.DeletedBy, "seed")
                .SetProperty(t => t.DeletedOn, DateTime.UtcNow), ct);

        await ctx.EmailTemplates.IgnoreQueryFilters()
            .Where(t => t.TenantId == null && !merinoKeys.Contains(t.TemplateKey))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TenantId, tenantId), ct);

        await ctx.EmailOutbox.IgnoreQueryFilters()
            .Where(o => o.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.TenantId, tenantId), ct);

        await ctx.InforEndpointMaps.IgnoreQueryFilters()
            .Where(m => m.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.TenantId, tenantId), ct);

        await ctx.InforSyncLogs.IgnoreQueryFilters()
            .Where(l => l.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.TenantId, tenantId), ct);

        await ctx.IntegrationErrors.IgnoreQueryFilters()
            .Where(e => e.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.TenantId, tenantId), ct);
    }

    /// <summary>Legacy masters + suppliers → tenant Merino, company 2000 (the legacy default company).</summary>
    private static async Task TagMastersAndSuppliersAsync(AppDbContext ctx, Guid tenantId, Guid company2000, CancellationToken ct)
    {
        await ctx.PaymentTerms.IgnoreQueryFilters()
            .Where(p => p.TenantId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.TenantId, tenantId)
                .SetProperty(p => p.TenantEntityId, company2000), ct);

        await ctx.DeliveryTerms.IgnoreQueryFilters()
            .Where(d => d.TenantId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.TenantId, tenantId)
                .SetProperty(d => d.TenantEntityId, company2000), ct);

        // Item is now company-scoped (promoted). Stamp legacy null-scope items to company 2000 so they
        // remain visible once Scope.FiltersEnabled flips. (Group/Unit links stay null for legacy rows.)
        await ctx.Items.IgnoreQueryFilters()
            .Where(i => i.TenantId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.TenantId, tenantId)
                .SetProperty(i => i.TenantEntityId, company2000), ct);

        // Tenant-scoped reference masters (Currency/Country/State/City/PostalCode + Unit/ItemGroup) — tag any
        // null-tenant rows to Merino. Unit/ItemGroup are company-scoped; also stamp company 2000.
        await ctx.Currencies.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId), ct);
        await ctx.Countries.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId), ct);
        await ctx.States.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId), ct);
        await ctx.Cities.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId), ct);
        await ctx.PostalCodes.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId), ct);
        await ctx.ItemGroups.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId).SetProperty(x => x.TenantEntityId, company2000), ct);
        await ctx.Units.IgnoreQueryFilters().Where(x => x.TenantId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantId, tenantId).SetProperty(x => x.TenantEntityId, company2000), ct);

        await ctx.Suppliers.IgnoreQueryFilters()
            .Where(s => s.TenantId == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.TenantId, tenantId)
                .SetProperty(s => s.TenantEntityId, company2000), ct);

        // G-seccodes mirror their supplier's scope.
        await ctx.Seccodes.IgnoreQueryFilters()
            .Where(s => s.TenantId == null && s.SupplierId != null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.TenantId, tenantId)
                .SetProperty(s => s.TenantEntityId, company2000), ct);

        // U-seccodes are tenant-scoped only.
        await ctx.Seccodes.IgnoreQueryFilters()
            .Where(s => s.TenantId == null && s.AppUserId != null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.TenantId, tenantId), ct);
    }

    /// <summary>
    /// Populate UserCompanyMap for supplier users — each supplier user gets a map to its mapped supplier's
    /// company so the always-on company filter doesn't hide its own data. One map per (user, company) is
    /// flagged isDefault. Tenant Admin needs none (implicit-all). NOT-EXISTS keeps it idempotent.
    /// </summary>
    private static async Task PopulateUserCompanyMapsAsync(AppDbContext ctx, Guid tenantId, DateTime now, CancellationToken ct)
    {
        // (userId, companyId) pairs derived from SupplierUserMap → Supplier.TenantEntityId.
        var pairs = await (
            from sum in ctx.SupplierUserMaps.IgnoreQueryFilters()
            join sup in ctx.Suppliers.IgnoreQueryFilters() on sum.SupplierId equals sup.Id
            where !sum.IsDeleted && sup.TenantEntityId != null
            select new { sum.AppUserId, CompanyId = sup.TenantEntityId!.Value })
            .Distinct()
            .ToListAsync(ct);

        if (pairs.Count == 0) return;

        var existing = await ctx.UserCompanyMaps.IgnoreQueryFilters()
            .Select(m => new { m.AppUserId, m.TenantEntityId })
            .ToListAsync(ct);
        var existingSet = existing.Select(e => (e.AppUserId, e.TenantEntityId)).ToHashSet();

        // Track which users already have ANY company so we only flag the first as default.
        var usersWithDefault = (await ctx.UserCompanyMaps.IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.IsDefault)
            .Select(m => m.AppUserId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var p in pairs)
        {
            if (existingSet.Contains((p.AppUserId, p.CompanyId))) continue;

            var isDefault = usersWithDefault.Add(p.AppUserId); // first company for this user → default
            ctx.UserCompanyMaps.Add(new UserCompanyMap
            {
                Id = DeterministicId.From("UserCompanyMap", $"{p.AppUserId}|{p.CompanyId}"),
                TenantId = tenantId,
                AppUserId = p.AppUserId,
                TenantEntityId = p.CompanyId,
                IsDefault = isDefault,
                CreatedBy = "seed",
                CreatedOn = now
            });
            existingSet.Add((p.AppUserId, p.CompanyId));
        }

        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// HEAVY: set-based scope stamping of the big proc aggregates via supplier / PO joins. One UPDATE…FROM
    /// per table; predicated on <c>tenantId IS NULL</c> for idempotency. No client round-trip. Ordered so
    /// downstream tables can chain off already-stamped parents (PO before DeliverySchedule/GoodsReceipt;
    /// Invoice before CreditDebitNote).
    /// </summary>
    private static async Task StampAggregateScopeAsync(string connectionString, Guid tenantId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var statements = new[]
        {
            // Direct supplierId joins
            @"UPDATE po SET po.tenantId=s.tenantId, po.tenantEntityId=s.tenantEntityId
              FROM [proc].[PurchaseOrder] po JOIN [supplier].[Supplier] s ON s.supplierId=po.supplierId
              WHERE po.tenantId IS NULL;",
            @"UPDATE a SET a.tenantId=s.tenantId, a.tenantEntityId=s.tenantEntityId
              FROM [proc].[Asn] a JOIN [supplier].[Supplier] s ON s.supplierId=a.supplierId
              WHERE a.tenantId IS NULL;",
            @"UPDATE i SET i.tenantId=s.tenantId, i.tenantEntityId=s.tenantEntityId
              FROM [proc].[Invoice] i JOIN [supplier].[Supplier] s ON s.supplierId=i.supplierId
              WHERE i.tenantId IS NULL;",
            @"UPDATE p SET p.tenantId=s.tenantId, p.tenantEntityId=s.tenantEntityId
              FROM [proc].[Payment] p JOIN [supplier].[Supplier] s ON s.supplierId=p.supplierId
              WHERE p.tenantId IS NULL;",
            @"UPDATE sv SET sv.tenantId=s.tenantId, sv.tenantEntityId=s.tenantEntityId
              FROM [supplier].[SupplierVerification] sv JOIN [supplier].[Supplier] s ON s.supplierId=sv.supplierId
              WHERE sv.tenantId IS NULL;",
            // Via Invoice
            @"UPDATE n SET n.tenantId=i.tenantId, n.tenantEntityId=i.tenantEntityId
              FROM [proc].[CreditDebitNote] n JOIN [proc].[Invoice] i ON i.invoiceId=n.invoiceId
              WHERE n.tenantId IS NULL;",
            // Via PurchaseOrder
            @"UPDATE ds SET ds.tenantId=po.tenantId, ds.tenantEntityId=po.tenantEntityId
              FROM [proc].[DeliverySchedule] ds JOIN [proc].[PurchaseOrder] po ON po.purchaseOrderId=ds.purchaseOrderId
              WHERE ds.tenantId IS NULL;",
            @"UPDATE cm SET cm.tenantId=po.tenantId, cm.tenantEntityId=po.tenantEntityId
              FROM [comm].[CommunicationMessage] cm JOIN [proc].[PurchaseOrder] po ON po.purchaseOrderId=cm.purchaseOrderId
              WHERE cm.tenantId IS NULL AND cm.purchaseOrderId IS NOT NULL;",
            // Via PurchaseOrderLine → PurchaseOrder
            @"UPDATE gr SET gr.tenantId=po.tenantId, gr.tenantEntityId=po.tenantEntityId
              FROM [proc].[GoodsReceipt] gr
              JOIN [proc].[PurchaseOrderLine] pl ON pl.purchaseOrderLineId=gr.purchaseOrderLineId
              JOIN [proc].[PurchaseOrder] po ON po.purchaseOrderId=pl.purchaseOrderId
              WHERE gr.tenantId IS NULL;",
            // DocumentUpload owned by a Supplier
            @"UPDATE d SET d.tenantId=s.tenantId, d.tenantEntityId=s.tenantEntityId
              FROM [doc].[DocumentUpload] d JOIN [supplier].[Supplier] s ON s.supplierId=d.ownerEntityId
              WHERE d.tenantId IS NULL AND d.ownerEntityType='Supplier';",
            // FALLBACK — standalone comm/doc rows with no PO/supplier link are still unscoped; the always-on
            // company filter would hide them entirely. Tag any remaining NULL-company rows to the legacy
            // default company 2000 (same convention as masters/suppliers). Runs LAST so the join-based passes
            // above take precedence; this only catches the leftovers. (Self-contained: tenant + company read
            // from the TenantEntity '2000' row, no parameters.)
            @"UPDATE cm SET cm.tenantId=te.tenantId, cm.tenantEntityId=te.tenantEntityId
              FROM [comm].[CommunicationMessage] cm
              CROSS JOIN (SELECT TOP 1 tenantId, tenantEntityId FROM [admin].[TenantEntity] WHERE code='2000') te
              WHERE cm.tenantEntityId IS NULL;",
            @"UPDATE d SET d.tenantId=te.tenantId, d.tenantEntityId=te.tenantEntityId
              FROM [doc].[DocumentUpload] d
              CROSS JOIN (SELECT TOP 1 tenantId, tenantEntityId FROM [admin].[TenantEntity] WHERE code='2000') te
              WHERE d.tenantEntityId IS NULL;",
        };

        foreach (var sql in statements)
        {
            Console.WriteLine($"[scope-backfill] {sql.Split('\n')[0].Trim()} ...");
            await conn.ExecuteAsync(new CommandDefinition(sql, commandTimeout: 1800, cancellationToken: ct));
        }

        Console.WriteLine("[scope-backfill] aggregate scope stamping complete.");
    }
}
