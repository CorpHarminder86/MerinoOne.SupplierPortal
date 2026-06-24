using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
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
/// HIGH fix — <c>MustChangePassword</c> is now a HARD server-side gate (<see cref="MerinoOne.SupplierPortal.Middlewares.MustChangePasswordGate"/>).
/// Previously a temp/default-password account (MustChangePassword=true) still received a fully-permissioned JWT and
/// could call every authorized API without ever changing the password. Now login mints a <c>must_change_password=true</c>
/// claim, and the gate rejects EVERY endpoint with 403 except the change-password remediation path (+ /auth/me, /auth/whoami).
/// <para>This seeds a dedicated MustChangePassword user (the shared harness users keep the flag false so the rest of the
/// suite stays un-gated) and asserts: a normal endpoint is 403 (gate, with the gate's message), while change-password and
/// /auth/me are NOT 403. A WRONG current password is used so the gate-pass is proven via a 400 (handler reached) WITHOUT
/// clearing the flag — keeping the test idempotent on re-run.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MustChangePasswordGateTests
{
    private readonly IntegrationTestFixture _fx;
    public MustChangePasswordGateTests(IntegrationTestFixture fx) => _fx = fx;

    private const string UserCode = "sec-pwdreset-a";

    private async Task SeedPwdResetUserAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.UserCode == UserCode))
            return;

        var now = DateTime.UtcNow;
        var userId = DeterministicId.From("sec.user", UserCode);
        db.AppUsers.Add(new AppUser
        {
            Id = userId, UserCode = UserCode, FullName = "Sec PwdReset A", Email = "sec-pwdreset-a@merino.local",
            PasswordHash = PasswordHasher.DeterministicHash(SecurityTestHarness.Password),
            IsInternal = true, IsMfaEnabled = false, IsActive = true,
            MustChangePassword = true, // the whole point — this principal is gated until it changes the password
            TenantId = IntegrationTestFixture.TenantId, CreatedBy = "seed", CreatedOn = now
        });
        // Give it the Admin role so a 403 can ONLY come from the gate (not from a missing permission).
        var adminRoleId = await db.Roles.IgnoreQueryFilters().Where(r => r.Name == "Admin").Select(r => r.Id).FirstAsync();
        db.UserRoles.Add(new UserRole
        {
            Id = DeterministicId.From("sec.userrole", $"{UserCode}|Admin"),
            AppUserId = userId, RoleId = adminRoleId, CreatedBy = "seed", CreatedOn = now
        });
        // Type-U seccode + self SecRight (well-formed principal, mirrors the harness).
        var seccodeId = DeterministicId.From("sec.seccode.u", UserCode);
        db.Seccodes.Add(new Seccode
        {
            Id = seccodeId, SeccodeType = SeccodeType.U, Name = UserCode + " default",
            AppUserId = userId, TenantId = IntegrationTestFixture.TenantId, CreatedBy = "seed", CreatedOn = now
        });
        db.SecRights.Add(new SecRight
        {
            Id = DeterministicId.From("sec.secright.u", UserCode), SeccodeId = seccodeId, UserCode = UserCode,
            CanRead = true, CanWrite = true, CreatedBy = "seed", CreatedOn = now
        });
        db.UserCompanyMaps.Add(new UserCompanyMap
        {
            Id = DeterministicId.From("sec.ucm", $"{UserCode}|fixture"),
            AppUserId = userId, TenantEntityId = IntegrationTestFixture.CompanyId, TenantId = IntegrationTestFixture.TenantId,
            IsDefault = true, AllSuppliers = false, CreatedBy = "seed", CreatedOn = now
        });
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Gate_blocks_normal_endpoints_but_allows_change_password_while_MustChangePassword()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await SeedPwdResetUserAsync();

        // Login mints a token carrying must_change_password=true (assert the login itself still succeeds).
        var token = await _fx.TokenForAsync(UserCode);
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // BLOCKED: a normal authorized endpoint → 403 from the gate (NOT 200, NOT a permission 403).
        var blocked = await client.GetAsync("/api/suppliers");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await blocked.Content.ReadAsStringAsync();
        body.Should().Contain("Password change required", "the 403 must be the gate's, not an authZ failure");

        // ALLOWED past the gate: change-password (wrong current password → 400 handler-reached, NOT 403 gate-blocked,
        // and does NOT clear the flag so the test stays idempotent).
        var change = await client.PostAsJsonAsync("/api/users/me/change-password",
            new { currentPassword = "Wrong@Current999", newPassword = "BrandNew@Pass123" });
        change.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        // ALLOWED past the gate: session rehydrate.
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
