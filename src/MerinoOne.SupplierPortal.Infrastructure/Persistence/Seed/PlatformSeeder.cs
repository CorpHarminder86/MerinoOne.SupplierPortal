using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// Seeds the single cross-tenant Platform Admin bootstrap user. The Platform Admin has TenantId = null
/// (bypasses the tenant filter), holds ONLY the PlatformAdmin role (no business-data permissions), and is
/// forced to change its password on first login. Idempotent — keyed on a deterministic Guid / userCode.
///
/// Password source: config "Seed:PlatformAdminPassword" (or env SEED_PLATFORM_ADMIN_PASSWORD) → fallback
/// to a known dev default. Always MustChangePassword = true so the seeded credential is single-use.
/// </summary>
public static class PlatformSeeder
{
    public const string UserCode = "platformadmin";
    public const string Email = "platformadmin@merino.local";
    private const string DefaultPassword = "Platform@123";

    public static async Task SeedAsync(AppDbContext ctx, IConfiguration cfg, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var userId = DeterministicId.From("AppUser", UserCode);

        // Idempotency: skip if the bootstrap user already exists (alive or tombstoned).
        if (await ctx.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.Id == userId, ct))
            return;

        var password = cfg["Seed:PlatformAdminPassword"]
                       ?? Environment.GetEnvironmentVariable("SEED_PLATFORM_ADMIN_PASSWORD")
                       ?? DefaultPassword;

        var role = await ctx.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.Name == "PlatformAdmin" && r.TenantId == null, ct);
        // PlatformAdmin role is seeded by PermissionSeeder (TenantId null). If absent, skip role grant
        // (PermissionSeeder runs first in SeedRunner, so this should always resolve).

        var seccodeId = DeterministicId.From("Seccode.U", UserCode);

        ctx.AppUsers.Add(new AppUser
        {
            Id = userId,
            TenantId = null,                 // cross-tenant — Platform Admin bypasses the tenant filter
            UserCode = UserCode,
            FullName = "Platform Administrator",
            Email = Email,
            PasswordHash = PasswordHasher.DeterministicHash(password),
            IsInternal = true,
            IsMfaEnabled = false,
            IsActive = true,
            MustChangePassword = true,
            CreatedBy = "seed",
            CreatedOn = now
        });

        if (role is not null)
        {
            ctx.UserRoles.Add(new UserRole
            {
                Id = DeterministicId.From("UserRole", $"{UserCode}|PlatformAdmin"),
                AppUserId = userId,
                RoleId = role.Id,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }

        // Default U-seccode + self SecRight so the Platform Admin is a first-class principal.
        ctx.Seccodes.Add(new Seccode
        {
            Id = seccodeId,
            SeccodeType = SeccodeType.U,
            Name = UserCode + " default",
            AppUserId = userId,
            TenantId = null,
            CreatedBy = "seed",
            CreatedOn = now
        });
        ctx.SecRights.Add(new SecRight
        {
            Id = DeterministicId.From("SecRight.U", UserCode),
            SeccodeId = seccodeId,
            UserCode = UserCode,
            CanRead = true,
            CanWrite = true,
            CreatedBy = "seed",
            CreatedOn = now
        });

        await ctx.SaveChangesAsync(ct);
    }
}
