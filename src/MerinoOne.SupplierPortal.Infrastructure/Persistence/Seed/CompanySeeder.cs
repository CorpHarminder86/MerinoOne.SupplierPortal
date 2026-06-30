using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R5 (TSD R5 Addendum §4.1–4.2 / §5 — Component 1, Company Master). Seeds one <see cref="Company"/> per existing
/// tenant company (<c>tenantEntityId</c>) and at least one <see cref="CompanyAddress"/> per Company carrying an
/// <c>erpCode</c>, so the inbound PO ship-to resolution (§6.2) and the integration tests can resolve a ship-to.
///
/// <para>Runs AFTER <see cref="TenantSeeder"/> (the tenant + the tenant-admin seccode must exist) and alongside
/// <see cref="AttachmentGovernanceSeeder"/>. The Company aggregate owns a seccode; like the other tenant-wide
/// config masters it is owned by the TENANT-ADMIN seccode so the admin's SecRight grants read/write under the
/// seccode RLS filter. Idempotent (deterministic ids, existence-checked). <c>CreatedBy = "seed"</c> short-circuits
/// the audit interceptor; tenant scope is stamped EXPLICITLY (the seed runs under the system principal).</para>
/// </summary>
public static class CompanySeeder
{
    public static Guid CompanyId(Guid tenantId, Guid tenantEntityId)
        => DeterministicId.From("Company", $"{tenantId}|{tenantEntityId}");

    public static Guid AddressId(Guid companyId, string erpCode)
        => DeterministicId.From("CompanyAddress", $"{companyId}|{erpCode}");

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenantId = TenantSeeder.TenantId;
        // The tenant-admin seccode (created by TenantSeeder) owns these tenant-wide config masters so the admin's
        // SecRight grants read/write under the seccode RLS filter — same pattern as AttachmentGovernanceSeeder.
        var seccodeId = DeterministicId.From("Seccode.U", TenantSeeder.AdminUserCode);

        // One Company per existing tenant company (TenantEntity). Name taken from the TenantEntity display name.
        var tenantEntities = await ctx.TenantEntities.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .Select(e => new { e.Id, e.Code, e.Name })
            .ToListAsync(ct);

        var existingCompanyIds = await ctx.Companies.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (var te in tenantEntities)
        {
            var companyId = CompanyId(tenantId, te.Id);
            if (existingCompanyIds.Contains(companyId)) continue;

            ctx.Companies.Add(new Company
            {
                Id = companyId,
                TenantId = tenantId,
                TenantEntityId = te.Id,
                Name = te.Name,
                IsActive = true,
                SeccodeId = seccodeId,
                CreatedBy = "seed",
                CreatedOn = now,
            });

            // At least one ship-to address per Company, with a deterministic erpCode keyed on the company code
            // (e.g. "DC-2000-01"), so the inbound PO ship-to resolves and the integration tests have a code to push.
            var erpCode = $"DC-{te.Code}-01";
            ctx.CompanyAddresses.Add(new CompanyAddress
            {
                Id = AddressId(companyId, erpCode),
                CompanyId = companyId,
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
