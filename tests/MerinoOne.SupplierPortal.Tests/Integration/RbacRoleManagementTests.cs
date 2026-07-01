using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Role/permission management end-to-end: create-role gating + validation, the assign-permissions
/// replace-set semantics INCLUDING the C1 re-save regression (delta apply must not hit the unique index),
/// and the API-served permission catalog. Each test creates its own throwaway role — the shared seed is
/// never mutated. Runs in the serial integration collection.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RbacRoleManagementTests
{
    private readonly IntegrationTestFixture _fx;
    public RbacRoleManagementTests(IntegrationTestFixture fx) => _fx = fx;

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    [SkippableFact]
    public async Task SuperAdmin_can_create_a_role_with_initial_permissions()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        var resp = await su.PostAsJsonAsync("/api/roles",
            new { name = NewName("mk"), permissionCodes = new[] { Perm.RoleRead, Perm.UserRead } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await resp.Content.ReadFromJsonAsync<Result<Guid>>())!.Data;
        id.Should().NotBe(Guid.Empty);

        var detail = (await (await su.GetAsync($"/api/roles/{id}")).Content.ReadFromJsonAsync<Result<RoleDetailDto>>())!.Data;
        detail!.PermissionCodes.Should().BeEquivalentTo(new[] { Perm.RoleRead, Perm.UserRead });
    }

    [SkippableFact]
    public async Task Creating_a_role_with_a_duplicate_name_is_409()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);
        var name = NewName("dup");

        (await su.PostAsJsonAsync("/api/roles", new { name, permissionCodes = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await su.PostAsJsonAsync("/api/roles", new { name, permissionCodes = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "a duplicate role name in the same tenant must 409");
    }

    [SkippableFact]
    public async Task Creating_a_role_with_an_unknown_permission_code_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        var resp = await su.PostAsJsonAsync("/api/roles",
            new { name = NewName("bad"), permissionCodes = new[] { "Not.A.Real.Permission" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "unknown permission codes are rejected by validation");
    }

    [SkippableFact]
    public async Task User_without_RoleWrite_cannot_create_a_role()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // Buyer holds neither Role.Read nor Role.Write, so it cannot create roles (SuperAdmin + Admin do).
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);

        var resp = await buyer.PostAsJsonAsync("/api/roles", new { name = NewName("nope"), permissionCodes = Array.Empty<string>() });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [SkippableFact]
    public async Task AssignPermissions_is_a_collision_free_replace_set_across_resaves()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        var id = (await (await su.PostAsJsonAsync("/api/roles",
            new { name = NewName("rp"), permissionCodes = new[] { Perm.RoleRead, Perm.UserRead } }))
            .Content.ReadFromJsonAsync<Result<Guid>>())!.Data;

        // Replace {RoleRead,UserRead} → {RoleRead,SettingsRead}: drops UserRead, keeps RoleRead, adds SettingsRead.
        (await su.PostAsJsonAsync($"/api/roles/{id}/permissions", new { permissionCodes = new[] { Perm.RoleRead, Perm.SettingsRead } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-save the SAME set again — this is the C1 trigger: re-inserting a still-present (RoleId,PermissionId)
        // against a soft-deleted row would violate the unique index without the filtered index + delta apply.
        (await su.PostAsJsonAsync($"/api/roles/{id}/permissions", new { permissionCodes = new[] { Perm.RoleRead, Perm.SettingsRead } }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "re-saving overlapping permissions must not hit a unique-constraint violation (C1)");

        // Re-add a previously-removed permission (UserRead) — exercises the resurrect path.
        (await su.PostAsJsonAsync($"/api/roles/{id}/permissions", new { permissionCodes = new[] { Perm.RoleRead, Perm.UserRead } }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "re-adding a previously-removed permission must resurrect, not collide");

        var detail = (await (await su.GetAsync($"/api/roles/{id}")).Content.ReadFromJsonAsync<Result<RoleDetailDto>>())!.Data;
        detail!.PermissionCodes.Should().BeEquivalentTo(new[] { Perm.RoleRead, Perm.UserRead }, "the final set is exactly the last replace-set");
    }

    [SkippableFact]
    public async Task Permission_catalog_excludes_service_and_platform_scopes()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        var resp = await su.GetAsync("/api/permissions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = (await resp.Content.ReadFromJsonAsync<Result<List<PermissionListItemDto>>>())!.Data!;
        list.Should().NotBeEmpty();
        list.Select(p => p.Code).Should().Contain(Perm.RoleRead);
        list.Should().OnlyContain(p => !p.Code.StartsWith("Integration.Inbound.") && !p.Code.StartsWith("Platform."),
            "the picker must hide service-to-service and platform-tier scopes");
    }

    [SkippableFact]
    public async Task Permission_catalog_requires_RoleRead()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // Supplier-role principal holds no Role.Read → the catalog endpoint is forbidden.
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        (await supplier.GetAsync("/api/permissions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
