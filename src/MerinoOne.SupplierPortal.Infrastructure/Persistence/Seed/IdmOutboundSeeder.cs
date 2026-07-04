using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.4 / D6. Seeds the outbound-IDM config per tenant: the three
/// <c>OutboundEndpointConfig</c> rows (Create/Update/Delete) and the default <c>IdmAttachmentTypeConfig</c> rows
/// (Invoice + ASN) from the repo-embedded JSONata expressions. Disabled by default (operators enable per tenant).
/// Idempotent: inserts when missing; on re-seed it overwrites an expression ONLY when the stored row is still
/// untouched since the last seed (current text hash == stored seed hash), then re-stamps the hash — a hand-edited
/// row is left alone (that difference is the drift the UI surfaces; "restore default" reconciles it).
/// </summary>
public static class IdmOutboundSeeder
{
    private const string Actor = "seed:r8-idm";

    private static readonly (string Key, string Method)[] Endpoints =
    {
        ("IDM.Item.Create", "POST"),
        ("IDM.Item.Update", "POST"),   // resolved O-R8-4: update in place via pid in the create-shaped payload
        ("IDM.Item.Delete", "DELETE"),
    };

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var defaults = new IdmDefaultExpressions();
        var tenantIds = await ctx.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            await SeedEndpointsAsync(ctx, tenantId, ct);
            await SeedTypeConfigsAsync(ctx, tenantId, defaults, ct);
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static async Task SeedEndpointsAsync(AppDbContext ctx, Guid tenantId, CancellationToken ct)
    {
        foreach (var (key, method) in Endpoints)
        {
            var exists = await ctx.Set<OutboundEndpointConfig>().IgnoreQueryFilters()
                .AnyAsync(e => e.TenantId == tenantId && e.EndpointKey == key && !e.IsDeleted, ct);
            if (exists) continue;

            ctx.Set<OutboundEndpointConfig>().Add(new OutboundEndpointConfig
            {
                TenantId = tenantId,
                TargetSystem = "IDM",
                EndpointKey = key,
                HttpMethod = method,
                RelativePath = "/IDM/api/items",
                AckParserKey = "IdmXml",
                DefaultAcl = "Public",
                IsEnabled = false,
                CreatedBy = Actor,
            });
        }
    }

    private static async Task SeedTypeConfigsAsync(AppDbContext ctx, Guid tenantId, IdmDefaultExpressions defaults, CancellationToken ct)
    {
        foreach (var entry in defaults.All)
        {
            if (!IdmDefaultExpressions.Seeds.TryGetValue(entry.IdmEntityType, out var seed)) continue;

            var existing = await ctx.Set<IdmAttachmentTypeConfig>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.AttachmentType == seed.AttachmentType && !c.IsDeleted, ct);

            if (existing is null)
            {
                ctx.Set<IdmAttachmentTypeConfig>().Add(new IdmAttachmentTypeConfig
                {
                    TenantId = tenantId,
                    AttachmentType = seed.AttachmentType,
                    IdmEntityType = entry.IdmEntityType,
                    EligibilityGateJson = seed.GateJson,
                    CreateMappingExpression = entry.CreateExpression,
                    CreateMappingSeedHash = entry.CreateHash,
                    MutateMappingExpression = entry.MutateExpression,
                    MutateMappingSeedHash = entry.MutateHash,
                    IsEnabled = false,
                    CreatedBy = Actor,
                });
                continue;
            }

            // Overwrite ONLY when still untouched since last seed (no hand-edit) AND the repo default changed.
            var touched = false;
            if (IdmDefaultExpressions.Hash(existing.CreateMappingExpression) == existing.CreateMappingSeedHash
                && existing.CreateMappingSeedHash != entry.CreateHash)
            {
                existing.CreateMappingExpression = entry.CreateExpression;
                existing.CreateMappingSeedHash = entry.CreateHash;
                touched = true;
            }
            if (existing.MutateMappingExpression is not null
                && IdmDefaultExpressions.Hash(existing.MutateMappingExpression) == existing.MutateMappingSeedHash
                && existing.MutateMappingSeedHash != entry.MutateHash)
            {
                existing.MutateMappingExpression = entry.MutateExpression;
                existing.MutateMappingSeedHash = entry.MutateHash;
                touched = true;
            }
            if (touched)
            {
                existing.UpdatedBy = Actor;
                existing.UpdatedOn = DateTime.UtcNow;
            }
        }
    }
}
