using System.Net;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// <c>GET /api/purchase-orders/lookup?q=&amp;supplierId=&amp;limit=</c> (2026-10-10) — the PO type-ahead behind lookup /
/// picker controls, through the REAL host.
///
/// <para>Each test owns a fresh <c>PO-LKP-{tag}-...</c> number space and seeds header-only POs straight into the DB (the
/// lookup never reads lines), so no assertion depends on what other suites left in the persistent test DB: every query
/// carries the test's own tag, which no other PO number contains. The POs are removed again afterwards (bulk DELETE of
/// this test's own tag), so the shared DB does not grow by the ~60 header rows a run would otherwise leave.</para>
///
/// <para>Scoping is NOT re-implemented by the lookup — it rides the always-on seccode / tenant / company filters like the
/// PO list. The Supplier-role user sees a PO only through a SecRight on its supplier's seccode, which is what the
/// supplier-scoped test relies on (the Admin role is privileged and sees every supplier's rows).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoLookupTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PoLookupTests(IntegrationTestFixture fx) => _fx = fx;

    // ── the 2-character floor ────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_query_shorter_than_two_characters_returns_an_empty_list()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        var supplier = await NewSupplierAsync(tag, grantTo: null);
        await SeedPosAsync(supplier, $"PO-LKP-{tag}-1");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // One character (once trimmed) matches the seeded PO's number too — yet the answer is an empty list.
        foreach (var shortQuery in new[] { "P", "%20P%20", "%20%20", "", "-", "1" })
        {
            var resp = await admin.GetAsync($"/api/purchase-orders/lookup?q={shortQuery}&supplierId={supplier.SupplierId}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
            var result = await Read<List<PoLookupItemDto>>(resp);
            result.Success.Should().BeTrue();
            result.Data.Should().BeEmpty(because: $"'{shortQuery}' is under two characters once trimmed");
        }

        // No q at all behaves the same...
        (await Read<List<PoLookupItemDto>>(await admin.GetAsync("/api/purchase-orders/lookup"))).Data.Should().BeEmpty();

        // ...and the floor is exactly two: the same PO is found by a two-character search.
        (await LookupAsync(admin, $"q=PO&supplierId={supplier.SupplierId}")).Should().ContainSingle(
            because: "two characters is enough to search");
    }

    // ── contains-match, case-insensitive, newest first, shaped as PoLookupItemDto ────────────────────────
    [SkippableFact]
    public async Task Contains_match_is_case_insensitive_and_returns_newest_po_date_first()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        var supplier = await NewSupplierAsync(tag, grantTo: null);
        var today = DateTime.UtcNow.Date;
        await SeedPosAsync(supplier,
            ($"PO-LKP-{tag}-OLD", today.AddDays(-5), PoStatus.Closed),
            ($"PO-LKP-{tag}-NEW", today, PoStatus.Released),
            ($"PO-LKP-{tag}-MID", today.AddDays(-2), PoStatus.Accepted),
            // Same date as MID: the tie-break is PoNumber ascending.
            ($"PO-LKP-{tag}-MID-A", today.AddDays(-2), PoStatus.Accepted),
            // Does NOT contain the search text, so it must stay out.
            ($"PO-UNRELATED-{tag}", today, PoStatus.Released));
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // Lower-case search text, matched inside the number (not at its start).
        var rows = await LookupAsync(admin, $"q=lkp-{tag.ToLowerInvariant()}");

        rows.Select(r => r.PoNumber).Should().Equal(
            $"PO-LKP-{tag}-NEW", $"PO-LKP-{tag}-MID", $"PO-LKP-{tag}-MID-A", $"PO-LKP-{tag}-OLD");

        var newest = rows[0];
        newest.Id.Should().NotBeEmpty();
        newest.SupplierName.Should().Be(supplier.LegalName, because: "SupplierName is the supplier's legal name, as the PO list shows it");
        newest.PoDate.Date.Should().Be(today);
        newest.Status.Should().Be(nameof(PoStatus.Released), because: "Status is the PoStatus enum NAME");
        rows.Single(r => r.PoNumber == $"PO-LKP-{tag}-OLD").Status.Should().Be(nameof(PoStatus.Closed));

        // Surrounding blanks around the text are ignored.
        (await LookupAsync(admin, $"q=%20%20LKP-{tag}-NEW%20")).Should().ContainSingle()
            .Which.PoNumber.Should().Be($"PO-LKP-{tag}-NEW");
    }

    // ── the text is matched literally: LIKE metacharacters in it mean nothing ────────────────────────────
    [SkippableFact]
    public async Task Search_text_is_matched_literally_not_as_a_pattern()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        var supplier = await NewSupplierAsync(tag, grantTo: null);
        await SeedPosAsync(supplier, $"PO-LKP-{tag}-1");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        (await LookupAsync(admin, $"q=PO_LKP-{tag}")).Should().BeEmpty(because: "'_' is not a one-character wildcard (it would match the '-')");
        (await LookupAsync(admin, $"q=LKP-{tag}%25")).Should().BeEmpty(because: "'%' is not a wildcard");
        (await LookupAsync(admin, $"q=%5BA-Z%5D-{tag}")).Should().BeEmpty(because: "'[A-Z]' is not a character class (it would match 'P-')");
        (await LookupAsync(admin, $"q=PO-LKP-{tag}")).Should().ContainSingle("the same text without the metacharacters does match");
    }

    // ── limit: default 10, honoured, capped at 50 ────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Limit_defaults_to_ten_is_honoured_and_is_capped_at_fifty()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        var supplier = await NewSupplierAsync(tag, grantTo: null);
        var today = DateTime.UtcNow.Date;
        // 55 POs, PO i dated i days ago → PO-00 is the newest and the result order is fully determined.
        await SeedPosAsync(supplier, Enumerable.Range(0, 55)
            .Select(i => ($"PO-LKP-{tag}-{i:D2}", today.AddDays(-i), PoStatus.Released)).ToArray());
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        string[] Newest(int n) => Enumerable.Range(0, n).Select(i => $"PO-LKP-{tag}-{i:D2}").ToArray();

        (await LookupAsync(admin, $"q=LKP-{tag}")).Select(r => r.PoNumber).Should().Equal(Newest(10),
            because: "no limit → the default of 10, newest first");
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=3")).Select(r => r.PoNumber).Should().Equal(Newest(3));
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=1")).Select(r => r.PoNumber).Should().Equal(Newest(1));
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=25")).Should().HaveCount(25);
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=50")).Should().HaveCount(50);

        // Out of range: a non-positive limit falls back to the default; an oversized one is capped at 50, never 55.
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=0")).Should().HaveCount(10);
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=-5")).Should().HaveCount(10);
        (await LookupAsync(admin, $"q=LKP-{tag}&limit=500")).Select(r => r.PoNumber).Should().Equal(Newest(50),
            because: "the hard cap is 50 whatever is asked for");
    }

    // ── supplierId narrows to one supplier ───────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task SupplierId_filter_narrows_to_one_suppliers_pos()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        var one = await NewSupplierAsync($"{tag}1", grantTo: null);
        var two = await NewSupplierAsync($"{tag}2", grantTo: null);
        await SeedPosAsync(one, $"PO-LKP-{tag}-ONE-1", $"PO-LKP-{tag}-ONE-2");
        await SeedPosAsync(two, $"PO-LKP-{tag}-TWO-1");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        (await LookupAsync(admin, $"q=LKP-{tag}")).Should().HaveCount(3, because: "no supplier filter → both suppliers' POs");

        var onlyOne = await LookupAsync(admin, $"q=LKP-{tag}&supplierId={one.SupplierId}");
        onlyOne.Select(r => r.PoNumber).Should().BeEquivalentTo(new[] { $"PO-LKP-{tag}-ONE-1", $"PO-LKP-{tag}-ONE-2" });
        onlyOne.Should().OnlyContain(r => r.SupplierName == one.LegalName);

        (await LookupAsync(admin, $"q=LKP-{tag}&supplierId={two.SupplierId}")).Should().ContainSingle()
            .Which.PoNumber.Should().Be($"PO-LKP-{tag}-TWO-1");
        (await LookupAsync(admin, $"q=LKP-{tag}&supplierId={Guid.NewGuid()}")).Should().BeEmpty();
    }

    // ── RLS: a supplier-scoped user sees only its own POs ────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_supplier_user_sees_only_its_own_suppliers_pos()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        await using var _ = Cleanup(tag);
        // Mine is readable by the Supplier-role user; Theirs is not.
        var mine = await NewSupplierAsync($"{tag}M", grantTo: SecurityTestHarness.Users.Supplier);
        var theirs = await NewSupplierAsync($"{tag}T", grantTo: null);
        await SeedPosAsync(mine, $"PO-LKP-{tag}-MINE-1", $"PO-LKP-{tag}-MINE-2");
        await SeedPosAsync(theirs, $"PO-LKP-{tag}-THEIRS-1");

        var supplierUser = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // The Admin (privileged) sees both suppliers' POs — proof the other supplier's PO really is reachable...
        (await LookupAsync(admin, $"q=LKP-{tag}")).Select(r => r.PoNumber).Should().BeEquivalentTo(
            new[] { $"PO-LKP-{tag}-MINE-1", $"PO-LKP-{tag}-MINE-2", $"PO-LKP-{tag}-THEIRS-1" });

        // ...yet the supplier user gets only its own, with no way to ask for the other's.
        var seen = await LookupAsync(supplierUser, $"q=LKP-{tag}");
        seen.Select(r => r.PoNumber).Should().BeEquivalentTo(new[] { $"PO-LKP-{tag}-MINE-1", $"PO-LKP-{tag}-MINE-2" });
        seen.Should().OnlyContain(r => r.SupplierName == mine.LegalName);

        (await LookupAsync(supplierUser, $"q=THEIRS&supplierId={theirs.SupplierId}")).Should().BeEmpty(
            because: "naming another supplier's id does not widen what the caller may see");
        (await LookupAsync(supplierUser, $"q=LKP-{tag}-THEIRS")).Should().BeEmpty();
    }

    // ── authentication ───────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Lookup_requires_an_authenticated_caller()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var anonymous = _fx.Factory.CreateClient();
        var resp = await anonymous.GetAsync("/api/purchase-orders/lookup?q=PO-LKP");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A short per-test tag (upper-case hex) that no other PO number in the DB contains.</summary>
    private static string NewTag() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private sealed record LookupSupplier(Guid SupplierId, Guid SeccodeId, string LegalName);

    /// <summary>A tagged supplier (and its G-seccode), optionally readable by one of the seeded API users.</summary>
    private async Task<LookupSupplier> NewSupplierAsync(string tag, string? grantTo)
    {
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, grantUserCode: grantTo, canWrite: false);
        return new LookupSupplier(supplier.SupplierId, supplier.SeccodeId, $"Test Supplier {tag}");
    }

    private Task SeedPosAsync(LookupSupplier supplier, params string[] poNumbers)
        => SeedPosAsync(supplier, poNumbers.Select(n => (n, DateTime.UtcNow.Date, PoStatus.Released)).ToArray());

    /// <summary>
    /// Header-only POs written straight to the DB (the lookup never reads lines). Mirrors the fixture's own PO seed:
    /// explicit scope columns + <c>CreatedBy="seed"</c> (the audit interceptor short-circuits). PO numbers are globally
    /// unique (<c>UQ_PurchaseOrder_poNumber</c>), which the per-test tag guarantees.
    /// </summary>
    private async Task SeedPosAsync(LookupSupplier supplier, params (string PoNumber, DateTime PoDate, PoStatus Status)[] pos)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.AddRange(pos.Select(p => new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            PoNumber = p.PoNumber,
            SupplierId = supplier.SupplierId,
            PoType = PoType.Material,
            PoDate = p.PoDate,
            PoStatus = p.Status,
            SeccodeId = supplier.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed",
            CreatedOn = now,
        }));
        await db.SaveChangesAsync();
    }

    /// <summary>Removes every PO this test seeded (all their numbers contain the tag) when the test ends, pass or fail.</summary>
    private IAsyncDisposable Cleanup(string tag) => new PoCleanup(_fx, tag);

    private sealed class PoCleanup(IntegrationTestFixture fx, string tag) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var scope = fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Bulk DELETE: no tracker, no soft-delete interceptor. The POs are header-only — no lines, schedules or ASNs hang off them.
            await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.PoNumber.Contains(tag)).ExecuteDeleteAsync();
        }
    }

    /// <summary>GETs the lookup with a raw query string and returns its rows (asserting the 200 envelope).</summary>
    private static async Task<List<PoLookupItemDto>> LookupAsync(HttpClient client, string query)
    {
        var resp = await client.GetAsync($"/api/purchase-orders/lookup?{query}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<List<PoLookupItemDto>>(resp);
        result.Success.Should().BeTrue();
        return result.Data!;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
