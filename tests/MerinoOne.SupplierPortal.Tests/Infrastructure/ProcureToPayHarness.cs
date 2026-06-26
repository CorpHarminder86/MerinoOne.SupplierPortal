using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// Procure-to-pay (PO → ASN → Invoice → GRN → Payment) test-data builders, layered ON TOP of the fixture's
/// money-path seed (never mutating it). Each helper takes a per-test <c>tag</c> so the created rows never
/// collide with the shared seed or another test.
///
/// <para>The serial/lot items honour the <c>inv.Item</c> XOR CHECK
/// (<c>CK_Item_serialized_xor_lot</c> = NOT (isSerialized=1 AND isLotControlled=1)) — at most one flag is set
/// per item. They are seeded under the fixture company "2000" (<see cref="IntegrationTestFixture.CompanyId"/>)
/// because the PO-line / ASN serial-lot resolution joins inv.Item by (TenantEntityId, Code), and the inbound
/// /items endpoint does NOT set those LN-fed flags — so the test owns them directly via the DbContext.</para>
///
/// <para>Every write carries <c>CreatedBy="seed"</c> so the audit interceptor short-circuits, and stamps the
/// scope columns explicitly (the fixture runs under the system principal). The money-path runs with the scope
/// gate OFF, so the supplier/admin API clients can load these rows without RLS interference.</para>
/// </summary>
public static class ProcureToPayHarness
{
    /// <summary>An item seeded into the fixture company, with its serialized / lot-controlled flags.</summary>
    public sealed record SeededItem(Guid ItemId, string ItemCode, bool IsSerialized, bool IsLotControlled);

    /// <summary>
    /// Seeds one <c>inv.Item</c> under the fixture company "2000" with the given control flags (serialized XOR
    /// lot-controlled — never both, per the CHECK). Idempotent by (company, code): a re-run returns the existing
    /// row. Code is uniquified by the caller's <paramref name="tag"/>.
    /// </summary>
    public static async Task<SeededItem> CreateItemAsync(
        this IntegrationTestFixture fx, string code, bool isSerialized = false, bool isLotControlled = false)
    {
        if (isSerialized && isLotControlled)
            throw new ArgumentException("An item cannot be both serialized AND lot-controlled (Item XOR CHECK).");

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var existing = await db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TenantEntityId == IntegrationTestFixture.CompanyId && i.Code == code);
        if (existing is not null)
            return new SeededItem(existing.Id, existing.Code, existing.IsSerialized, existing.IsLotControlled);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
            Code = code,
            Description = $"Test item {code}",
            IsActive = true,
            IsSerialized = isSerialized,
            IsLotControlled = isLotControlled,
            CreatedBy = "seed",
            CreatedOn = now,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return new SeededItem(item.Id, item.Code, item.IsSerialized, item.IsLotControlled);
    }

    /// <summary>
    /// Seeds one <c>proc.Tax</c> master under the fixture company "2000" (the inbound /taxes endpoint needs the
    /// Tax scope the fixture key doesn't carry, so the test owns it directly). Returns the Tax id. PO/invoice
    /// lines resolve <c>taxId</c> by (TenantEntityId, Code). Idempotent by (company, code).
    /// </summary>
    public static async Task<Guid> CreateTaxAsync(this IntegrationTestFixture fx, string code, decimal? rate = null)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var existing = await db.Taxes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantEntityId == IntegrationTestFixture.CompanyId && t.Code == code);
        if (existing is not null) return existing.Id;

        var tax = new Tax
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
            Code = code,
            Description = $"Test tax {code}",
            TaxRate = rate,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = now,
        };
        db.Taxes.Add(tax);
        await db.SaveChangesAsync();
        return tax.Id;
    }

    /// <summary>
    /// Grants the given user a (canRead, canWrite) SecRight on a supplier's G-seccode. Idempotent on
    /// (seccodeId, userCode). Lets a Supplier-role API client own a tagged supplier's procurement chain.
    /// </summary>
    public static async Task GrantSecRightAsync(
        this IntegrationTestFixture fx, Guid seccodeId, string userCode, bool canWrite = true)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var existing = await db.SecRights.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.SeccodeId == seccodeId && r.UserCode == userCode);
        if (existing is not null)
        {
            existing.CanRead = true;
            existing.CanWrite = canWrite;
            existing.UpdatedBy = "seed";
            existing.UpdatedOn = now;
        }
        else
        {
            db.SecRights.Add(new SecRight
            {
                Id = Guid.NewGuid(), SeccodeId = seccodeId, UserCode = userCode,
                CanRead = true, CanWrite = canWrite, CreatedBy = "seed", CreatedOn = now,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// R4 (2026-06-26) — D3. Flips the tenant-wide <c>Fulfilment.EnforceOverShipGuard</c> setting ON (the over-ship
    /// CEILING REJECTION is off by default as a rollout control) and invalidates the singleton settings cache, so
    /// the ASN create/update guard rejects over-ship/below-shipped attempts. Returns a disposable that restores the
    /// setting to OFF + re-invalidates. ALWAYS pair the enable with the returned disposable (serial collection
    /// execution guarantees no other test observes the ON state).
    /// </summary>
    public static async Task<IAsyncDisposable> EnableOverShipGuardAsync(this IntegrationTestFixture fx)
    {
        await SetOverShipGuardAsync(fx, enabled: true);
        return new OverShipGuardScope(fx);
    }

    private static async Task SetOverShipGuardAsync(IntegrationTestFixture fx, bool enabled)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var setting = await db.SystemSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Category == FulfilmentKeys.Category && s.SettingKey == FulfilmentKeys.EnforceOverShipGuard);
        var value = enabled ? "true" : "false";
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Id = Guid.NewGuid(),
                Category = FulfilmentKeys.Category, SettingKey = FulfilmentKeys.EnforceOverShipGuard, SettingValue = value,
                IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
        }
        else
        {
            setting.SettingValue = value;
            setting.IsActive = true;
            setting.UpdatedBy = "seed";
            setting.UpdatedOn = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Drop the cached singleton snapshot so the next read re-reads the new value.
        foreach (var inv in fx.Factory.Services.GetServices<ISettingsCacheInvalidator>())
            inv.InvalidateCategory(FulfilmentKeys.Category);
    }

    private sealed class OverShipGuardScope : IAsyncDisposable
    {
        private readonly IntegrationTestFixture _fx;
        public OverShipGuardScope(IntegrationTestFixture fx) => _fx = fx;
        public async ValueTask DisposeAsync() => await SetOverShipGuardAsync(_fx, enabled: false);
    }
}
