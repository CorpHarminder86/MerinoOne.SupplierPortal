using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class UserSeeder
{
    public record UserSpec(string UserCode, string FullName, string Email, string Role);

    public static readonly IReadOnlyList<UserSpec> Specs = new[]
    {
        new UserSpec("sadmin1", "Super Admin One",   "sadmin1@merino.local",  RoleNames.SuperAdmin),
        new UserSpec("sadmin2", "Super Admin Two",   "sadmin2@merino.local",  RoleNames.SuperAdmin),
        new UserSpec("admin1",  "Admin One",         "admin1@merino.local",   RoleNames.Admin),
        new UserSpec("admin2",  "Admin Two",         "admin2@merino.local",   RoleNames.Admin),
        new UserSpec("buyer1",  "Buyer One",         "buyer1@merino.local",   RoleNames.Buyer),
        new UserSpec("buyer2",  "Buyer Two",         "buyer2@merino.local",   RoleNames.Buyer),
        new UserSpec("fin1",    "Finance One",       "finance1@merino.local", RoleNames.Finance),
        new UserSpec("fin2",    "Finance Two",       "finance2@merino.local", RoleNames.Finance),
        new UserSpec("sup1",    "Supplier User One", "supplier1@merino.local",RoleNames.Supplier),
        new UserSpec("sup2",    "Supplier User Two", "supplier2@merino.local",RoleNames.Supplier),
        new UserSpec("ro1",     "Read Only One",     "readonly1@merino.local",RoleNames.ReadOnly),
        new UserSpec("ro2",     "Read Only Two",     "readonly2@merino.local",RoleNames.ReadOnly),
    };

    public const string DefaultPassword = "Merino@123";

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var hash = PasswordHasher.DeterministicHash(DefaultPassword);

        var existing = await ctx.AppUsers.Select(u => u.UserCode).ToListAsync(ct);
        var roleMap = await ctx.Roles.ToDictionaryAsync(r => r.Name, r => r.Id, ct);

        foreach (var spec in Specs)
        {
            if (existing.Contains(spec.UserCode)) continue;
            var isInternal = spec.Role != RoleNames.Supplier;
            var userId = DeterministicId.From("AppUser", spec.UserCode);
            var seccodeId = DeterministicId.From("Seccode.U", spec.UserCode);
            var secRightId = DeterministicId.From("SecRight.U", spec.UserCode);

            var user = new AppUser
            {
                Id = userId,
                UserCode = spec.UserCode,
                FullName = spec.FullName,
                Email = spec.Email,
                PasswordHash = hash,
                IsInternal = isInternal,
                IsMfaEnabled = isInternal,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            };
            ctx.AppUsers.Add(user);

            if (roleMap.TryGetValue(spec.Role, out var roleId))
            {
                ctx.UserRoles.Add(new UserRole
                {
                    Id = DeterministicId.From("UserRole", $"{spec.UserCode}|{spec.Role}"),
                    AppUserId = userId,
                    RoleId = roleId,
                    CreatedBy = "seed",
                    CreatedOn = now
                });
            }

            var seccode = new Seccode
            {
                Id = seccodeId,
                SeccodeType = SeccodeType.U,
                Name = spec.UserCode + " default",
                AppUserId = userId,
                CreatedBy = "seed",
                CreatedOn = now
            };
            ctx.Seccodes.Add(seccode);

            ctx.SecRights.Add(new SecRight
            {
                Id = secRightId,
                SeccodeId = seccodeId,
                UserCode = spec.UserCode,
                CanRead = true,
                CanWrite = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }

        await ctx.SaveChangesAsync(ct);
    }
}
