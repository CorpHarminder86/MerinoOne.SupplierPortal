using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R5/R13. Seeds per tenant company (<see cref="TenantEntity"/>): (1) one WAREHOUSE
/// <see cref="CompanyAddress"/> carrying an <c>erpCode</c> so the inbound PO-LINE warehouse resolution and the
/// integration tests can resolve a warehouse, and (2) the company's single BASE address (no erpCode) shown on
/// the PO screen beside the customer name. (The duplicate admin.Company was dropped — CompanyAddress hangs
/// directly off the TenantEntity.)
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

    // R13 — the company's single base address id (no erpCode; keyed on a fixed "BASE" discriminator).
    public static Guid BaseAddressId(Guid tenantEntityId)
        => DeterministicId.From("CompanyAddress", $"{tenantEntityId}|BASE");

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
            // (1) WAREHOUSE — a deterministic erpCode keyed on the company code (e.g. "DC-2000-01"), so an inbound
            // PO line's warehouse resolves and the integration tests have a code to push. IsBaseAddress = false.
            var erpCode = $"DC-{te.Code}-01";
            var addressId = AddressId(te.Id, erpCode);
            if (!existingAddressIds.Contains(addressId))
            {
                ctx.CompanyAddresses.Add(new CompanyAddress
                {
                    Id = addressId,
                    TenantEntityId = te.Id,
                    AddressName = $"{te.Name} — Distribution Centre",
                    ErpCode = erpCode,
                    AddressType = "Warehouse",
                    AddressLine1 = "1 Industrial Estate",
                    City = "Mumbai",
                    State = "Maharashtra",
                    Pincode = "400001",
                    Country = "India",
                    IsActive = true,
                    IsBaseAddress = false,
                    CreatedBy = "seed",
                    CreatedOn = now,
                });
            }

            // (2) BASE — the company's own identity/customer address (NO erpCode), shown on the PO screen beside
            // the customer name. Exactly one per company (filtered-unique index enforces it).
            var baseId = BaseAddressId(te.Id);
            if (!existingAddressIds.Contains(baseId))
            {
                ctx.CompanyAddresses.Add(new CompanyAddress
                {
                    Id = baseId,
                    TenantEntityId = te.Id,
                    AddressName = $"{te.Name} — Head Office",
                    ErpCode = null,
                    AddressType = "Base",
                    AddressLine1 = "Plot 5, Sector 62",
                    City = "Noida",
                    State = "Uttar Pradesh",
                    Pincode = "201301",
                    Country = "India",
                    IsActive = true,
                    IsBaseAddress = true,
                    CreatedBy = "seed",
                    CreatedOn = now,
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
