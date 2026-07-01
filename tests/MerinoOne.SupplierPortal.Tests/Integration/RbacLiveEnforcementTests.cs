using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// The headline "no relogin" guarantees, proven with the SAME bearer token across a permission change and
/// an account deactivation:
/// <list type="bullet">
///   <item>Granting a permission to a user's role is enforced on the user's NEXT request — no relogin.</item>
///   <item>Revoking it is enforced on the next request — no relogin.</item>
///   <item>Deactivating the account locks it out on the next request (401), not at token expiry.</item>
/// </list>
/// Each test builds its OWN throwaway role + login-able user, so the shared seed is never mutated.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RbacLiveEnforcementTests
{
    private readonly IntegrationTestFixture _fx;
    public RbacLiveEnforcementTests(IntegrationTestFixture fx) => _fx = fx;

    private static string Rnd => Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task Granting_then_revoking_a_permission_takes_effect_without_relogin()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        // Throwaway role with NO permissions, and a user holding it.
        var roleId = await CreateRoleAsync(su, Array.Empty<string>());
        var userCode = $"live-{Rnd}";
        await SeedLoginableUserAsync(userCode, roleId);

        // Log in ONCE; reuse this token for every subsequent request (no relogin anywhere below).
        var client = await BearerClientAsync(userCode);

        // Baseline: no Role.Read → forbidden.
        (await client.GetAsync("/api/roles")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "the user starts with no permissions");

        // Grant Role.Read to the role.
        (await su.PostAsJsonAsync($"/api/roles/{roleId}/permissions", new { permissionCodes = new[] { Perm.RoleRead } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // SAME token now passes — the grant applied on the next request.
        (await client.GetAsync("/api/roles")).StatusCode
            .Should().Be(HttpStatusCode.OK, "a granted permission must apply on the next request without re-login");

        // Revoke it again.
        (await su.PostAsJsonAsync($"/api/roles/{roleId}/permissions", new { permissionCodes = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // SAME token is blocked again — the revoke applied on the next request.
        (await client.GetAsync("/api/roles")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a revoked permission must apply on the next request without re-login");
    }

    [SkippableFact]
    public async Task Deactivating_a_user_locks_them_out_on_the_next_request()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var su = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var roleId = await CreateRoleAsync(su, new[] { Perm.RoleRead });
        var userCode = $"deact-{Rnd}";
        var userId = await SeedLoginableUserAsync(userCode, roleId);

        var client = await BearerClientAsync(userCode);

        // Works while active.
        (await client.GetAsync("/api/roles")).StatusCode
            .Should().Be(HttpStatusCode.OK, "an active user with Role.Read can read roles");

        // Deactivate via the API (Admin holds User.Write).
        (await admin.PostAsync($"/api/users/{userId}/deactivate", content: null)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // SAME token is now rejected — immediate lockout, no waiting for token expiry.
        (await client.GetAsync("/api/roles")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a deactivated user must be locked out on their next request");
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static async Task<Guid> CreateRoleAsync(HttpClient su, string[] permissionCodes)
    {
        var resp = await su.PostAsJsonAsync("/api/roles",
            new { name = $"live-{Guid.NewGuid():N}"[..20], permissionCodes });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<Result<Guid>>())!.Data;
    }

    private async Task<HttpClient> BearerClientAsync(string userCode)
    {
        var token = await _fx.TokenForAsync(userCode);
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Insert a login-able, non-MFA user under the fixture tenant, mapped to <paramref name="roleId"/>.</summary>
    private async Task<Guid> SeedLoginableUserAsync(string userCode, Guid roleId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();

        db.AppUsers.Add(new AppUser
        {
            Id = userId, UserCode = userCode, FullName = userCode, Email = userCode + "@merino.local",
            PasswordHash = PasswordHasher.DeterministicHash(SecurityTestHarness.Password),
            IsInternal = true, IsMfaEnabled = false, IsActive = true,
            TenantId = IntegrationTestFixture.TenantId, CreatedBy = "test", CreatedOn = now
        });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), AppUserId = userId, RoleId = roleId, CreatedBy = "test", CreatedOn = now
        });

        var seccodeId = Guid.NewGuid();
        db.Seccodes.Add(new Seccode
        {
            Id = seccodeId, SeccodeType = SeccodeType.U, Name = userCode + " default",
            AppUserId = userId, TenantId = IntegrationTestFixture.TenantId, CreatedBy = "test", CreatedOn = now
        });
        db.SecRights.Add(new SecRight
        {
            Id = Guid.NewGuid(), SeccodeId = seccodeId, UserCode = userCode,
            CanRead = true, CanWrite = true, CreatedBy = "test", CreatedOn = now
        });

        await db.SaveChangesAsync();
        return userId;
    }
}
