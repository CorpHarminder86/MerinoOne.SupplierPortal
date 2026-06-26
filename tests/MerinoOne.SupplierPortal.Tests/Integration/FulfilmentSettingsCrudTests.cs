using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §7.4 / §8.5 / D5, Phase 5a (Settings CRUD surface). Proves the admin Settings endpoints:
/// <list type="bullet">
///   <item>the supplier-item tolerance GRID resolves override ?? item-master via the shared resolver (a present
///         override wins; an absent override inherits the master);</item>
///   <item>the attachment-policy GRID returns tenant defaults + a supplier override with the D5 supplier-wins
///         EFFECTIVE requirement.</item>
/// </list>
/// Runs as the Admin user (Settings.Read/Write; privileged for the seccode RLS filter so the seeded config rows
/// are visible). The scope-filter gate stays OFF (fixture money-path), so the tenant filter doesn't hide rows.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FulfilmentSettingsCrudTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public FulfilmentSettingsCrudTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task SupplierItem_grid_resolves_override_or_inherits_master()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(
            tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

        // Two item masters: one will get a supplier override, one will inherit.
        var overriddenItemId = await SeedItemAsync($"OVR-{tag}", masterPct: 10m);
        var inheritedItemId = await SeedItemAsync($"INH-{tag}", masterPct: 7m);

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // Override the first item at 2% (supplier wins); leave the second to inherit.
        var put = await client.PutAsJsonAsync("/api/settings/supplier-items",
            new UpsertSupplierItemToleranceRequest(supplier.SupplierId, overriddenItemId, 2m));
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await put.Content.ReadAsStringAsync());
        var putBody = await Read<SupplierItemToleranceDto>(put);
        putBody.Success.Should().BeTrue();
        putBody.Data!.SupplierOverridePct.Should().Be(2m);
        putBody.Data.ResolvedTolerancePct.Should().Be(2m, because: "the override wins");

        // GET the grid and assert both resolutions.
        var get = await client.GetAsync($"/api/settings/supplier-items?supplierId={supplier.SupplierId}");
        var grid = (await Read<List<SupplierItemToleranceDto>>(get)).Data!;

        var overridden = grid.Single(r => r.ItemId == overriddenItemId);
        overridden.ItemMasterTolerancePct.Should().Be(10m);
        overridden.SupplierOverridePct.Should().Be(2m);
        overridden.ResolvedTolerancePct.Should().Be(2m);
        overridden.SupplierItemId.Should().NotBeNull();

        var inherited = grid.Single(r => r.ItemId == inheritedItemId);
        inherited.SupplierOverridePct.Should().BeNull(because: "no override row → inherit");
        inherited.ResolvedTolerancePct.Should().Be(7m, because: "resolved falls back to the item master");
        inherited.SupplierItemId.Should().BeNull();

        // DELETE the override → reverts to inherit (resolved == master).
        var del = await client.DeleteAsync($"/api/settings/supplier-items/{overridden.SupplierItemId}");
        del.StatusCode.Should().Be(HttpStatusCode.OK, because: await del.Content.ReadAsStringAsync());

        var get2 = await client.GetAsync($"/api/settings/supplier-items?supplierId={supplier.SupplierId}");
        var grid2 = (await Read<List<SupplierItemToleranceDto>>(get2)).Data!;
        var reverted = grid2.Single(r => r.ItemId == overriddenItemId);
        reverted.SupplierOverridePct.Should().BeNull();
        reverted.ResolvedTolerancePct.Should().Be(10m, because: "deleting the override reverts to the master");
    }

    [SkippableFact]
    public async Task AttachmentPolicy_grid_resolves_two_tier_supplier_wins()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await _fx.ClearPoliciesAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(
            tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

        // Tenant default: Asn·PackingSlip = Mandatory. Supplier override: Asn·PackingSlip = Optional.
        await _fx.SeedPoliciesAsync(
            new AttachmentGovernanceHarness.PolicySpec("Asn", "PackingSlip", AttachmentRequirement.Mandatory),
            new AttachmentGovernanceHarness.PolicySpec("Asn", "PackingSlip", AttachmentRequirement.Optional, supplier.SupplierId),
            new AttachmentGovernanceHarness.PolicySpec("Asn", "TestCertificate", AttachmentRequirement.Mandatory, supplier.SupplierId));

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // GET with the supplier → tenant defaults + this supplier's overrides.
        var get = await client.GetAsync($"/api/settings/attachment-policies?entityCode=Asn&supplierId={supplier.SupplierId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK, because: await get.Content.ReadAsStringAsync());
        var rows = (await Read<List<AttachmentPolicyDto>>(get)).Data!;

        // PackingSlip: tenant default Mandatory + supplier override Optional → EFFECTIVE Optional (supplier wins).
        var packingRows = rows.Where(r => r.AttachmentTypeCode == "PackingSlip").ToList();
        packingRows.Should().HaveCount(2, because: "tenant default + supplier override both returned");
        packingRows.Should().OnlyContain(r => r.EffectiveRequirement == "Optional",
            because: "the supplier override wins for every PackingSlip row");
        packingRows.Should().Contain(r => r.SupplierId == null && r.Requirement == "Mandatory");
        packingRows.Should().Contain(r => r.SupplierId == supplier.SupplierId && r.Requirement == "Optional");

        // TestCertificate: only a supplier override Mandatory (no tenant default) → effective Mandatory.
        var testCert = rows.Single(r => r.AttachmentTypeCode == "TestCertificate");
        testCert.SupplierId.Should().Be(supplier.SupplierId);
        testCert.EffectiveRequirement.Should().Be("Mandatory");

        // GET WITHOUT the supplier → tenant defaults only (no override rows leak in).
        var getDefaults = await client.GetAsync("/api/settings/attachment-policies?entityCode=Asn");
        var defaults = (await Read<List<AttachmentPolicyDto>>(getDefaults)).Data!;
        defaults.Should().OnlyContain(r => r.SupplierId == null);
        defaults.Single(r => r.AttachmentTypeCode == "PackingSlip").EffectiveRequirement.Should().Be("Mandatory",
            because: "with no supplier context the tenant default is the effective value");

        await _fx.ClearPoliciesAsync();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private async Task<Guid> SeedItemAsync(string code, decimal masterPct)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.Items.Add(new Item
        {
            Id = id, Code = code, Description = $"Test item {code}", IsActive = true,
            OverShipTolerancePct = masterPct,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
