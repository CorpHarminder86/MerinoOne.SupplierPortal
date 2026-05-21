using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class PermissionSeeder
{
    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // 1. Permissions
        var existingPerms = await ctx.Permissions.Select(p => p.Code).ToListAsync(ct);
        var newPerms = PermissionCatalog.All
            .Where(p => !existingPerms.Contains(p.Code))
            .Select(p => new Permission
            {
                Id = DeterministicId.From("Permission", p.Code),
                Code = p.Code,
                Name = p.Name,
                Description = p.Description,
                Module = p.Module,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newPerms.Count > 0)
            ctx.Permissions.AddRange(newPerms);

        // 2. Roles
        var existingRoles = await ctx.Roles.Select(r => r.Name).ToListAsync(ct);
        var newRoles = PermissionCatalog.Roles
            .Where(r => !existingRoles.Contains(r))
            .Select(r => new Role
            {
                Id = DeterministicId.From("Role", r),
                Name = r,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newRoles.Count > 0)
            ctx.Roles.AddRange(newRoles);

        await ctx.SaveChangesAsync(ct);

        // 3. RolePermissions — keyed on (roleId, permissionId)
        var roleMap = await ctx.Roles.ToDictionaryAsync(r => r.Name, r => r.Id, ct);
        var permMap = await ctx.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id, ct);
        var existingPairs = await ctx.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionId })
            .ToListAsync(ct);
        var existingSet = existingPairs.Select(p => (p.RoleId, p.PermissionId)).ToHashSet();

        var newPairs = new List<RolePermission>();
        foreach (var (code, roles) in PermissionCatalog.Matrix)
        {
            if (!permMap.TryGetValue(code, out var permId)) continue;
            foreach (var role in roles)
            {
                if (!roleMap.TryGetValue(role, out var roleId)) continue;
                if (existingSet.Contains((roleId, permId))) continue;
                newPairs.Add(new RolePermission
                {
                    Id = DeterministicId.From("RolePermission", $"{role}|{code}"),
                    RoleId = roleId,
                    PermissionId = permId,
                    CreatedBy = "seed",
                    CreatedOn = now
                });
            }
        }
        if (newPairs.Count > 0)
            ctx.RolePermissions.AddRange(newPairs);

        await ctx.SaveChangesAsync(ct);
    }
}
