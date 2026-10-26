using FluentAssertions;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R17 — <see cref="TenantSeeder"/> share-group seeding must be idempotent on the NATURAL keys the unique indexes enforce, not
/// just on its own deterministic Ids. Adding <c>SharedEndpoint.Carrier</c> to the seeded endpoints made <c>seed</c> crash on a
/// dev DB where an admin had already built a Carrier group for the same source company through the Share Groups UI
/// (<c>UQ_CompanyShareGroup_tenant_endpoint_source</c>). The seeder now leaves an admin-created (or admin-deleted) group alone:
/// no duplicate group, and no members added to a group it did not create.
///
/// <para>The test DB is persistent and the Merino tenant (a SEPARATE tenant from the fixture's own) is not part of the fixture
/// seed, so every test runs the real seeder inside ONE database transaction that is ROLLED BACK in a <c>finally</c>: whatever
/// the seeder or the arrange step wrote — the Merino tenant, its companies, groups, endpoint maps, email templates — never
/// outlives the test, whether or not the Merino tenant already existed in this database. The host's background workers use
/// their own connections and are unaffected beyond waiting on the uncommitted rows for the few seconds a test takes.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TenantSeederShareGroupIdempotencyTests
{
    private readonly IntegrationTestFixture _fx;
    public TenantSeederShareGroupIdempotencyTests(IntegrationTestFixture fx) => _fx = fx;

    private static Guid Tenant => TenantSeeder.TenantId;
    private static Guid Co(string code) => TenantSeeder.CompanyId(code);

    /// <summary>Runs <paramref name="body"/> against a fresh scope inside a transaction that is always rolled back.</summary>
    private async Task InRolledBackTransactionAsync(Func<AppDbContext, Task> body)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await body(db);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    private static Task<List<CompanyShareGroup>> GroupsAsync(AppDbContext db, SharedEndpoint endpoint) =>
        db.CompanyShareGroups.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.TenantId == Tenant && g.Endpoint == endpoint).ToListAsync();

    private static Task<List<CompanyShareGroupMember>> MembersAsync(AppDbContext db, SharedEndpoint endpoint) =>
        db.CompanyShareGroupMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == Tenant && m.Endpoint == endpoint).ToListAsync();

    /// <summary>Bulk hard-delete of the tenant's groups + members for one endpoint (no change tracker, no soft-delete interceptor).</summary>
    private static async Task HardDeleteTopologyAsync(AppDbContext db, SharedEndpoint endpoint)
    {
        await db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .Where(m => m.TenantId == Tenant && m.Endpoint == endpoint).ExecuteDeleteAsync();
        await db.CompanyShareGroups.IgnoreQueryFilters()
            .Where(g => g.TenantId == Tenant && g.Endpoint == endpoint).ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    private static CompanyShareGroup AdminGroup(Guid id, SharedEndpoint endpoint, string sourceCode) => new()
    {
        Id = id, TenantId = Tenant, Endpoint = endpoint, SourceTenantEntityId = Co(sourceCode),
        Name = $"{endpoint} (created in the Share Groups UI)", IsEnabled = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    private static CompanyShareGroupMember AdminMember(Guid groupId, SharedEndpoint endpoint, string companyCode) => new()
    {
        Id = Guid.NewGuid(), TenantId = Tenant, CompanyShareGroupId = groupId, MemberTenantEntityId = Co(companyCode),
        Endpoint = endpoint, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    /// <summary>
    /// The step AFTER share groups is the proof the share-group step did not throw. Knock the two R17 inbound maps out first so
    /// the post-run assertion shows THIS run (re)created them, instead of passing on rows that were already there.
    /// </summary>
    private static async Task RemoveR17EndpointMapsAsync(AppDbContext db)
    {
        await db.InforEndpointMaps.IgnoreQueryFilters()
            .Where(m => m.TenantId == Tenant && m.Direction == SyncDirection.Inbound
                        && (m.EntityName == nameof(SharedEndpoint.Carrier) || m.EntityName == nameof(TransactionalInboundEntity.AsnUpdate)))
            .ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AssertR17EndpointMapsEnabledAsync(AppDbContext db)
    {
        var maps = await db.InforEndpointMaps.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == Tenant && m.Direction == SyncDirection.Inbound
                        && (m.EntityName == nameof(SharedEndpoint.Carrier) || m.EntityName == nameof(TransactionalInboundEntity.AsnUpdate)))
            .ToListAsync();
        maps.Select(m => m.EntityName).Should().BeEquivalentTo(new[] { "Carrier", "AsnUpdate" });
        maps.Should().OnlyContain(m => m.IsEnabled && !m.IsDeleted);
    }

    // ── the observed failure: an admin-created Carrier group (random Id) for source 2000 ────────────────
    [SkippableFact]
    public async Task A_carrier_group_an_admin_created_for_the_same_source_is_left_untouched()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await InRolledBackTransactionAsync(async db =>
        {
            await TenantSeeder.SeedAsync(db); // baseline: Merino tenant + companies + the full default topology

            // Arrange the dev-DB state: Carrier topology replaced by an admin's own group for source 2000 holding 2000 + 3000.
            await HardDeleteTopologyAsync(db, SharedEndpoint.Carrier);
            var adminGroupId = Guid.NewGuid();
            var admin2000 = AdminMember(adminGroupId, SharedEndpoint.Carrier, "2000");
            var admin3000 = AdminMember(adminGroupId, SharedEndpoint.Carrier, "3000");
            db.CompanyShareGroups.Add(AdminGroup(adminGroupId, SharedEndpoint.Carrier, "2000"));
            db.CompanyShareGroupMembers.AddRange(admin2000, admin3000);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await RemoveR17EndpointMapsAsync(db);

            Func<Task> reseed = () => TenantSeeder.SeedAsync(db);
            await reseed.Should().NotThrowAsync();

            // Exactly ONE live group for (tenant, Carrier, 2000) and it is the admin's, not a seeded duplicate.
            var carrierGroups = await GroupsAsync(db, SharedEndpoint.Carrier);
            carrierGroups.Where(g => g.SourceTenantEntityId == Co("2000") && !g.IsDeleted)
                .Should().ContainSingle().Which.Id.Should().Be(adminGroupId);
            carrierGroups.Where(g => g.SourceTenantEntityId == Co("5000") && !g.IsDeleted).Should().ContainSingle();

            // The admin's group is theirs: its own membership rows survive untouched and the seeder adds NOTHING to it —
            // company 4000 (missing from their group) is not added to it, nor anywhere else for Carrier source 2000.
            var members = await MembersAsync(db, SharedEndpoint.Carrier);
            members.Where(m => m.CompanyShareGroupId == adminGroupId).Select(m => m.Id)
                .Should().BeEquivalentTo(new[] { admin2000.Id, admin3000.Id }, "the seeder must never add members to an admin-made group");
            members.Should().NotContain(m => m.MemberTenantEntityId == Co("4000"));
            members.Where(m => !m.IsDeleted).GroupBy(m => m.MemberTenantEntityId).Should().OnlyContain(g => g.Count() == 1);

            // Every endpoint still has exactly one live group per topology source.
            foreach (var endpoint in new[] { SharedEndpoint.PaymentTerm, SharedEndpoint.DeliveryTerm, SharedEndpoint.Carrier,
                                             SharedEndpoint.Unit, SharedEndpoint.ItemGroup, SharedEndpoint.Item })
            {
                var groups = await GroupsAsync(db, endpoint);
                groups.Where(g => !g.IsDeleted).GroupBy(g => g.SourceTenantEntityId).Should().OnlyContain(g => g.Count() == 1);
                groups.Select(g => g.SourceTenantEntityId).Should().Contain(new[] { Co("2000"), Co("5000") });
            }

            // The step after share groups ran: Carrier + AsnUpdate inbound maps exist and are enabled.
            await AssertR17EndpointMapsEnabledAsync(db);
        });
    }

    // ── an admin deleted the group on purpose: leave that source alone for that endpoint ────────────────
    [SkippableFact]
    public async Task A_soft_deleted_group_is_respected_and_its_topology_is_not_resurrected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await InRolledBackTransactionAsync(async db =>
        {
            await TenantSeeder.SeedAsync(db);

            // The admin deleted the Carrier group for source 2000 (group + its memberships soft-deleted).
            await db.CompanyShareGroupMembers.IgnoreQueryFilters()
                .Where(m => m.TenantId == Tenant && m.Endpoint == SharedEndpoint.Carrier
                            && m.CompanyShareGroup!.SourceTenantEntityId == Co("2000"))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true));
            await db.CompanyShareGroups.IgnoreQueryFilters()
                .Where(g => g.TenantId == Tenant && g.Endpoint == SharedEndpoint.Carrier && g.SourceTenantEntityId == Co("2000"))
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.IsDeleted, true));
            db.ChangeTracker.Clear();
            await RemoveR17EndpointMapsAsync(db);

            Func<Task> reseed = () => TenantSeeder.SeedAsync(db);
            await reseed.Should().NotThrowAsync();

            var groups = await GroupsAsync(db, SharedEndpoint.Carrier);
            groups.Where(g => g.SourceTenantEntityId == Co("2000")).Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
            groups.Where(g => g.SourceTenantEntityId == Co("5000") && !g.IsDeleted).Should().ContainSingle();

            var members = await MembersAsync(db, SharedEndpoint.Carrier);
            members.Where(m => !m.IsDeleted).Select(m => m.MemberTenantEntityId)
                .Should().BeEquivalentTo(new[] { Co("5000"), Co("6000") }, "the deleted 2000/3000/4000 topology must not be resurrected");

            await AssertR17EndpointMapsEnabledAsync(db);
        });
    }

    // ── a company already live in ANOTHER group for the endpoint is never moved ─────────────────────────
    [SkippableFact]
    public async Task A_company_already_in_another_live_group_for_the_endpoint_is_not_added_again()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await InRolledBackTransactionAsync(async db =>
        {
            await TenantSeeder.SeedAsync(db);

            // The admin grouped 3000 + 4000 under source 3000 — a different source than the seeded 2000 topology.
            await HardDeleteTopologyAsync(db, SharedEndpoint.Carrier);
            var adminGroupId = Guid.NewGuid();
            db.CompanyShareGroups.Add(AdminGroup(adminGroupId, SharedEndpoint.Carrier, "3000"));
            db.CompanyShareGroupMembers.AddRange(
                AdminMember(adminGroupId, SharedEndpoint.Carrier, "3000"),
                AdminMember(adminGroupId, SharedEndpoint.Carrier, "4000"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await RemoveR17EndpointMapsAsync(db);

            Func<Task> reseed = () => TenantSeeder.SeedAsync(db);
            await reseed.Should().NotThrowAsync();

            var seededGroupId = DeterministicId.From("CompanyShareGroup", $"{TenantSeeder.TenantName}|{SharedEndpoint.Carrier}|2000");
            var members = await MembersAsync(db, SharedEndpoint.Carrier);
            members.Where(m => !m.IsDeleted).GroupBy(m => m.MemberTenantEntityId).Should().OnlyContain(g => g.Count() == 1);
            members.Single(m => m.MemberTenantEntityId == Co("3000")).CompanyShareGroupId.Should().Be(adminGroupId);
            members.Single(m => m.MemberTenantEntityId == Co("4000")).CompanyShareGroupId.Should().Be(adminGroupId);
            members.Single(m => m.MemberTenantEntityId == Co("2000")).CompanyShareGroupId.Should().Be(seededGroupId);

            await AssertR17EndpointMapsEnabledAsync(db);
        });
    }

    // ── the plain re-run is still a no-op ───────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Reseeding_an_already_seeded_tenant_adds_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await InRolledBackTransactionAsync(async db =>
        {
            await TenantSeeder.SeedAsync(db);
            var groupsBefore = await db.CompanyShareGroups.IgnoreQueryFilters().CountAsync(g => g.TenantId == Tenant);
            var membersBefore = await db.CompanyShareGroupMembers.IgnoreQueryFilters().CountAsync(m => m.TenantId == Tenant);
            db.ChangeTracker.Clear();

            Func<Task> reseed = () => TenantSeeder.SeedAsync(db);
            await reseed.Should().NotThrowAsync();

            (await db.CompanyShareGroups.IgnoreQueryFilters().CountAsync(g => g.TenantId == Tenant)).Should().Be(groupsBefore);
            (await db.CompanyShareGroupMembers.IgnoreQueryFilters().CountAsync(m => m.TenantId == Tenant)).Should().Be(membersBefore);
        });
    }
}
