using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FluentValidation;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Commands;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Integration;
using UsersPage = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Users.UserListItemDto>;
using RolesPage = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Users.RoleListItemDto>;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Cross-tenant isolation for the user-principal admin handlers that read with <c>IgnoreQueryFilters()</c>
/// (which drops the tenant filter for the whole query). A tenant-A admin must not read, or act on, tenant-B
/// users / roles / documents by GUID. One test per leak class:
/// <list type="bullet">
///   <item>LN pin-sample builds a foreign-tenant entity into the config's sample JSON (supplier bank details).</item>
///   <item>User list / detail.</item>
///   <item>Role list / detail.</item>
///   <item>User write paths (update, password, (de)activate, role assign/remove, supplier + company maps).</item>
///   <item>Role write paths (rename, permissions) + assigning a foreign or the PlatformAdmin role.</item>
/// </list>
/// Each test seeds FRESH tagged tenant-B rows (the test DB persists between runs), so the red phase mutates
/// only throwaway data, never the seeded <c>sec-admin-b</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CrossTenantAdminIsolationTests
{
    private readonly IntegrationTestFixture _fx;
    public CrossTenantAdminIsolationTests(IntegrationTestFixture fx) => _fx = fx;

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ---------------------------------------------------------------------------------------------------
    // 1. LN pin-sample
    // ---------------------------------------------------------------------------------------------------

    private sealed class TenantAUser : ICurrentUser
    {
        public string UserCode => "xt-int-admin";
        public string? UserName => "XT Integration Admin";
        public IReadOnlyCollection<string> Roles => new[] { RoleNames.Admin };
        public IReadOnlyCollection<string> Permissions => new[] { Perm.IntegrationAdmin };
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => true;
        public bool HasPermission(string code) => true;
        public Guid? TenantId => IntegrationTestFixture.TenantId;
        public bool IsPlatformAdmin => false;
    }

    /// <summary>A fresh tenant-A Supplier config (unique transaction type, Held) so seeded configs stay pristine.</summary>
    private async Task<Guid> SeedTenantASupplierConfigAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.OutboundIntegrationConfigs.Add(new OutboundIntegrationConfig
        {
            Id = id,
            TenantId = IntegrationTestFixture.TenantId,
            Kind = OutboundIntegrationKind.Transaction,
            TransactionType = $"XT-{Tag()}",
            PortalEntity = LnPortalEntity.Supplier,
            EndpointPath = "xt/never-dispatched",
            HttpVerb = "POST",
            DispatchMode = OutboundDispatchMode.Held,
            RequestMappingExpr = "$",
            CreatedBy = "xt-test",
            CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task HardDeleteConfigAsync(Guid id)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OutboundIntegrationConfigs.IgnoreQueryFilters().Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    private async Task<bool> PinAsync(Guid configId, Guid entityId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builders = scope.ServiceProvider.GetRequiredService<ILnInputDocumentBuilderRegistry>();
        return await new PinLnSampleDocumentCommandHandler(db, new TenantAUser(), builders)
            .Handle(new PinLnSampleDocumentCommand(configId, entityId), CancellationToken.None);
    }

    private async Task<string?> SampleOfAsync(Guid configId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == configId).Select(c => c.SampleDocumentJson).FirstAsync();
    }

    [SkippableFact]
    public async Task PinSample_refuses_a_foreign_tenant_entity_but_pins_an_own_tenant_one()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var configId = await SeedTenantASupplierConfigAsync();
        try
        {
            // Tenant-A Integration.Admin pins the tenant-B supplier → must be refused, nothing stored.
            await Assert.ThrowsAsync<ValidationException>(() => PinAsync(configId, SecurityTestHarness.SupplierBId));
            (await SampleOfAsync(configId)).Should().BeNull(
                because: "a tenant-B supplier (incl. bank details) must never be frozen into a tenant-A config sample");

            // Own-tenant supplier still pins (dispatcher-facing builder behaviour unchanged).
            (await PinAsync(configId, IntegrationTestFixture.SupplierId)).Should().BeTrue();
            (await SampleOfAsync(configId)).Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await HardDeleteConfigAsync(configId);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Users / roles — fresh tagged rows
    // ---------------------------------------------------------------------------------------------------

    private sealed record Seeded(Guid UserBId, string UserBCode, Guid RoleBId, string RoleBName, Guid UserAId, string PasswordHashB);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tag = Tag();
        var now = DateTime.UtcNow;

        var roleB = new Role
        {
            Id = Guid.NewGuid(), TenantId = SecurityTestHarness.TenantBId, Name = $"xt-role-b-{tag}",
            CreatedBy = "xt-test", CreatedOn = now
        };
        var hashB = PasswordHasher.DeterministicHash("Xt@TenantB123");
        var userB = new AppUser
        {
            Id = Guid.NewGuid(), TenantId = SecurityTestHarness.TenantBId, UserCode = $"xt-b-{tag}",
            FullName = $"XT User B {tag}", Email = $"xt-b-{tag}@merino.local", PasswordHash = hashB,
            IsInternal = true, IsMfaEnabled = false, IsActive = true, CreatedBy = "xt-test", CreatedOn = now
        };
        var userA = new AppUser
        {
            Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId, UserCode = $"xt-a-{tag}",
            FullName = $"XT User A {tag}", Email = $"xt-a-{tag}@merino.local",
            PasswordHash = PasswordHasher.DeterministicHash("Xt@TenantA123"),
            IsInternal = true, IsMfaEnabled = false, IsActive = true, CreatedBy = "xt-test", CreatedOn = now
        };
        db.Roles.Add(roleB);
        db.AppUsers.AddRange(userB, userA);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), AppUserId = userB.Id, RoleId = roleB.Id, CreatedBy = "xt-test", CreatedOn = now
        });
        await db.SaveChangesAsync();
        return new Seeded(userB.Id, userB.UserCode, roleB.Id, roleB.Name, userA.Id, hashB);
    }

    private Task<HttpClient> AdminAAsync() =>
        _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

    [SkippableFact]
    public async Task UserList_and_detail_do_not_expose_another_tenants_user()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await using var _ = await _fx.EnableScopeFiltersAsync();
        var s = await SeedAsync();
        var admin = await AdminAAsync();

        var list = await admin.GetFromJsonAsync<Result<UsersPage>>(
            $"/api/users?search={s.UserBCode}&pageSize=200");
        list!.Data!.Items.Should().NotContain(u => u.Id == s.UserBId,
            because: "a tenant-A admin must not list tenant-B users");

        (await admin.GetAsync($"/api/users/{s.UserBId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Own-tenant user still visible.
        (await admin.GetAsync($"/api/users/{s.UserAId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task RoleList_and_detail_do_not_expose_another_tenants_role()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await using var _ = await _fx.EnableScopeFiltersAsync();
        var s = await SeedAsync();
        var admin = await AdminAAsync();

        // Roles have no search → walk every page so absence is real, not pagination.
        var seen = new List<RoleListItemDto>();
        for (var page = 1; ; page++)
        {
            var r = (await admin.GetFromJsonAsync<Result<RolesPage>>(
                $"/api/roles?page={page}&pageSize=200"))!.Data!;
            seen.AddRange(r.Items);
            if (page >= r.TotalPages) break;
        }
        seen.Should().NotContain(r => r.Id == s.RoleBId, because: "a tenant-A admin must not list tenant-B roles");
        seen.Should().NotContain(r => r.Name == RoleNames.PlatformAdmin,
            because: "the cross-tenant PlatformAdmin role is not a tenant role");
        seen.Should().Contain(r => r.Name == RoleNames.Admin, because: "built-in roles stay visible");

        (await admin.GetAsync($"/api/roles/{s.RoleBId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task User_write_paths_404_for_another_tenants_user()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await using var _ = await _fx.EnableScopeFiltersAsync();
        var s = await SeedAsync();
        var admin = await AdminAAsync();
        var u = $"/api/users/{s.UserBId}";

        var calls = new (string What, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("update", () => admin.PutAsJsonAsync(u, new UpdateUserRequest("Hijacked", $"hijack-{Tag()}@merino.local", true, false))),
            ("set password", () => admin.PostAsJsonAsync($"{u}/password", new SetPasswordRequest("Hijack@12345678"))),
            ("deactivate", () => admin.PostAsync($"{u}/deactivate", null)),
            ("reactivate", () => admin.PostAsync($"{u}/reactivate", null)),
            ("assign role", () => admin.PostAsJsonAsync($"{u}/roles", new AssignRoleRequest(DeterministicId.From("Role", RoleNames.Admin)))),
            ("remove role", () => admin.DeleteAsync($"{u}/roles/{s.RoleBId}")),
            ("map supplier", () => admin.PostAsJsonAsync($"{u}/supplier-maps",
                new MapSupplierRequest(IntegrationTestFixture.SupplierId, false, IntegrationTestFixture.CompanyId))),
            ("set supplier maps", () => admin.PutAsJsonAsync($"{u}/supplier-maps?tenantEntityId={IntegrationTestFixture.CompanyId}",
                new SetSupplierMapsRequest(new[] { IntegrationTestFixture.SupplierId }, false))),
            ("grant company", () => admin.PostAsJsonAsync($"{u}/company-maps", new GrantCompanyRequest(IntegrationTestFixture.CompanyId))),
        };

        foreach (var (what, call) in calls)
            (await call()).StatusCode.Should().Be(HttpStatusCode.NotFound, because: $"'{what}' on a tenant-B user must 404");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await db.AppUsers.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == s.UserBId);
        after.FullName.Should().NotBe("Hijacked");
        after.PasswordHash.Should().Be(s.PasswordHashB, because: "a tenant-A admin must not reset a tenant-B password");
        after.IsActive.Should().BeTrue();
        (await db.UserRoles.IgnoreQueryFilters().CountAsync(x => x.AppUserId == s.UserBId && !x.IsDeleted)).Should().Be(1);
        (await db.SupplierUserMaps.IgnoreQueryFilters().AnyAsync(x => x.AppUserId == s.UserBId && !x.IsDeleted)).Should().BeFalse();
        (await db.UserCompanyMaps.IgnoreQueryFilters().AnyAsync(x => x.AppUserId == s.UserBId && !x.IsDeleted)).Should().BeFalse();
    }

    [SkippableFact]
    public async Task Role_write_paths_404_for_another_tenants_role_and_PlatformAdmin_is_not_assignable()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await using var _ = await _fx.EnableScopeFiltersAsync();
        var s = await SeedAsync();
        var admin = await AdminAAsync();

        (await admin.PutAsJsonAsync($"/api/roles/{s.RoleBId}", new UpdateRoleRequest($"hijack-{Tag()}")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync($"/api/roles/{s.RoleBId}/permissions", new AssignPermissionsRequest(new[] { Perm.UserWrite })))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Own-tenant user, foreign-tenant role.
        (await admin.PostAsJsonAsync($"/api/users/{s.UserAId}/roles", new AssignRoleRequest(s.RoleBId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Own-tenant user, the cross-tenant PlatformAdmin role (deterministic id) — escalation.
        (await admin.PostAsJsonAsync($"/api/users/{s.UserAId}/roles",
                new AssignRoleRequest(DeterministicId.From("Role", RoleNames.PlatformAdmin))))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var role = await db.Roles.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == s.RoleBId);
        role.Name.Should().Be(s.RoleBName);
        (await db.RolePermissions.IgnoreQueryFilters().AnyAsync(rp => rp.RoleId == s.RoleBId && !rp.IsDeleted)).Should().BeFalse();
        (await db.UserRoles.IgnoreQueryFilters().AnyAsync(ur => ur.AppUserId == s.UserAId && !ur.IsDeleted)).Should().BeFalse(
            because: "neither the tenant-B role nor PlatformAdmin may be granted to a tenant-A user");
    }
}
