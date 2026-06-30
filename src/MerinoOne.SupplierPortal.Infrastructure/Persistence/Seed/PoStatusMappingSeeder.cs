using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R5 (TSD R5 Addendum §4.7 / §11 — Component 7). Seeds the signed-off ERP→portal PO-status mapping per tenant
/// into <c>proc.PoStatusMapping</c>. The seeded default REPRODUCES the R4 hardcoded <c>Modify → Released</c>
/// behaviour (<c>modified → Released</c> is one of the rows), so out-of-the-box behaviour is unchanged after the
/// R4 hardcode is externalised to config.
///
/// <para><b>Seed (§4.7):</b> Draft ← {Draft, Created}; Released ← {Approved, Released, Sent, modified};
/// Cancelled ← {Canceled, Blocked}; Closed ← {Closed}; Delivered ← {Delivered}. Targets are restricted to the
/// ERP-driven subset (§11.2); the supplier/fulfilment-driven statuses are never mapped.</para>
///
/// <para>Runs AFTER <see cref="TenantSeeder"/> (tenant + tenant-admin seccode must exist) alongside
/// <see cref="CompanySeeder"/>. The rows are tenant-scoped config masters owned by the tenant-admin seccode (so
/// the admin's SecRight grants Settings read/write under the seccode RLS). Idempotent — deterministic ids,
/// existence-checked. <c>CreatedBy="seed"</c> short-circuits the audit interceptor; tenant scope is stamped
/// explicitly (the seed runs under the system principal).</para>
/// </summary>
public static class PoStatusMappingSeeder
{
    /// <summary>The signed-off seed: one (erpStatus → portal PoStatus) pair per row (§4.7).</summary>
    public static readonly (string ErpStatus, PoStatus PoStatus)[] Seed =
    {
        ("Draft",    PoStatus.Draft),
        ("Created",  PoStatus.Draft),
        ("Approved", PoStatus.Released),
        ("Released", PoStatus.Released),
        ("Sent",     PoStatus.Released),
        ("modified", PoStatus.Released),   // reproduces the R4 hardcoded Modify → Released behaviour.
        ("Canceled", PoStatus.Cancelled),
        ("Blocked",  PoStatus.Cancelled),
        ("Closed",   PoStatus.Closed),
        ("Delivered", PoStatus.Delivered),
    };

    public static Guid MappingId(Guid tenantId, string erpStatus)
        => DeterministicId.From("PoStatusMapping", $"{tenantId}|{erpStatus.ToLowerInvariant()}");

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenantId = TenantSeeder.TenantId;
        // Same tenant-admin seccode the other tenant-wide config masters use (CompanySeeder / AttachmentGovernance).
        var seccodeId = DeterministicId.From("Seccode.U", TenantSeeder.AdminUserCode);

        var existingIds = await ctx.PoStatusMappings.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Id)
            .ToListAsync(ct);

        foreach (var (erpStatus, poStatus) in Seed)
        {
            var id = MappingId(tenantId, erpStatus);
            if (existingIds.Contains(id)) continue;

            ctx.PoStatusMappings.Add(new PoStatusMapping
            {
                Id = id,
                TenantId = tenantId,
                ErpStatus = erpStatus,
                PoStatus = poStatus.ToString(),
                IsActive = true,
                SeccodeId = seccodeId,
                CreatedBy = "seed",
                CreatedOn = now,
            });
        }

        await ctx.SaveChangesAsync(ct);
    }
}
