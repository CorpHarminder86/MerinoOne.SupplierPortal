using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R5 (TSD R5 Addendum §4.2 / §5 — Component 1 / [[r5-consolidation]]). Seeds at least one
/// <see cref="CompanyAddress"/> per existing tenant company (<see cref="TenantEntity"/>) carrying an
/// <c>erpCode</c>, so the inbound PO ship-to resolution (§6.2) and the integration tests can resolve a ship-to.
/// (The duplicate admin.Company was dropped — CompanyAddress now hangs directly off the TenantEntity.)
///
/// <para>Runs AFTER <see cref="TenantSeeder"/> (the tenant companies must exist). CompanyAddress is an
/// AuditableEntity with NO seccode of its own — it scopes via its owning TenantEntity — so nothing is stamped
/// with a seccode here. Idempotent (deterministic ids, existence-checked). <c>CreatedBy = "seed"</c>
/// short-circuits the audit interceptor; the seed runs under the system principal.</para>
/// </summary>
public static class CompanySeeder
{
    public static Guid AddressId(Guid tenantEntityId, string erpCode)
        => DeterministicId.From("CompanyAddress", $"{tenantEntityId}|{erpCode}");

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenantId = TenantSeeder.TenantId;

        // One ship-to address per existing tenant company (TenantEntity). Name taken from the TenantEntity.
        var tenantEntities = await ctx.TenantEntities.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .Select(e => new { e.Id, e.Code, e.Name })
            .ToListAsync(ct);

        // CompanyAddress has no tenant column of its own; the deterministic id folds in tenantEntityId so ids never
        // collide across tenants — an all-rows existence check is sufficient for idempotency.
        var existingAddressIds = await ctx.CompanyAddresses.IgnoreQueryFilters()
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var te in tenantEntities)
        {
            // A deterministic erpCode keyed on the company code (e.g. "DC-2000-01"), so the inbound PO ship-to
            // resolves and the integration tests have a code to push.
            var erpCode = $"DC-{te.Code}-01";
            var addressId = AddressId(te.Id, erpCode);
            if (existingAddressIds.Contains(addressId)) continue;

            ctx.CompanyAddresses.Add(new CompanyAddress
            {
                Id = addressId,
                TenantEntityId = te.Id,
                AddressName = $"{te.Name} — Distribution Centre",
                ErpCode = erpCode,
                AddressType = "Shipping",
                AddressLine1 = "1 Industrial Estate",
                City = "Mumbai",
                State = "Maharashtra",
                Pincode = "400001",
                Country = "India",
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now,
            });
        }

        await ctx.SaveChangesAsync(ct);
    }
}
