using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// RLS tenant-isolation: data created under tenant B must NOT surface to a tenant-A principal, for two
/// representative reads — the supplier list (gated by the always-on tenant + company query filters) and the
/// sync-log list (explicitly tenant-guarded in the handler).
///
/// <para><b>Gate-enabled-vs-backfill tension</b>: the always-on tenant/company filters are gated by the global
/// <c>Scope.FiltersEnabled</c> SystemSetting (singleton cache). The fixture's money-path tests run with the
/// gate OFF (the legitimate backfill window). These RLS tests flip the gate ON for the duration of the test
/// ONLY, via <see cref="SecurityTestHarness.EnableScopeFiltersAsync"/>, and restore OFF + invalidate the cache
/// in the <c>await using</c> finally. The single shared xUnit collection runs serially, so no money-path test
/// ever observes the gate ON — the fixture's backfill-window assumption is preserved.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TenantIsolationRlsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public TenantIsolationRlsTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- read #1: supplier list (always-on tenant + company filter) --------------------

    [SkippableFact]
    public async Task SupplierList_does_not_leak_tenant_B_rows_to_tenant_A_admin()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await using var _ = await _fx.EnableScopeFiltersAsync();

        // Tenant-A admin, active company = the fixture company "2000". With the gate ON, the tenant filter ANDs
        // TenantId == A and the company filter ANDs TenantEntityId == company2000 — so the tenant-B supplier
        // (TenantId == B, company 9000) is excluded by BOTH.
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await client.GetAsync("/api/suppliers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await Read<List<SupplierListItemDto>>(resp);
        result.Success.Should().BeTrue();
        var codes = result.Data!.Select(s => s.SupplierCode).ToList();

        codes.Should().NotContain(SecurityTestHarness.SupplierBCode,
            because: "a tenant-A admin must never see a tenant-B supplier when the scope filters enforce");
        codes.Should().Contain("SUP-INT-01",
            because: "the tenant-A fixture supplier (company 2000) IS visible to the tenant-A admin");
    }

    [SkippableFact]
    public async Task SupplierList_does_not_leak_tenant_A_rows_to_tenant_B_admin()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await using var _ = await _fx.EnableScopeFiltersAsync();

        // The mirror direction: tenant-B admin, active company = company "9000". It sees its own supplier only.
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.AdminB, SecurityTestHarness.CompanyBId);

        var resp = await client.GetAsync("/api/suppliers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await Read<List<SupplierListItemDto>>(resp);
        var codes = result.Data!.Select(s => s.SupplierCode).ToList();

        codes.Should().Contain(SecurityTestHarness.SupplierBCode,
            because: "tenant B sees its own supplier");
        codes.Should().NotContain("SUP-INT-01",
            because: "tenant B must never see the tenant-A fixture supplier");
    }

    // -------------------- read #2: sync-log list (explicit tenant guard in the handler) --------------------

    [SkippableFact]
    public async Task SyncLog_list_does_not_leak_tenant_B_rows_to_tenant_A_admin()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await using var _ = await _fx.EnableScopeFiltersAsync();

        // Integration.Read is held by Admin. The sync-log query guards x.TenantId == caller-tenant explicitly.
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // Page large enough to be sure the tenant-B row's absence is real, not pagination.
        var resp = await client.GetAsync("/api/integration/sync-log?page=1&pageSize=500");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await Read<PagedResult<InforSyncLogDto>>(resp);
        result.Success.Should().BeTrue();

        result.Data!.Items.Should().NotContain(x => x.Id == SecurityTestHarness.SyncLogBId,
            because: "the tenant-B sync-log row must never appear in a tenant-A admin's list");
    }

    // -------------------- helper --------------------

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
