using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class SupplierSeeder
{
    public record SupplierSpec(string Code, string LegalName, string Gst, string Pan, RegistrationStatus Status);

    public static readonly IReadOnlyList<SupplierSpec> Specs = new[]
    {
        new SupplierSpec("S0001", "Mega Polymers Pvt Ltd",      "29ABCDE1234F1Z5", "ABCDE1234F", RegistrationStatus.Active),
        new SupplierSpec("S0002", "Apex Steel Industries",      "27FGHIJ5678K1L9", "FGHIJ5678K", RegistrationStatus.Active),
        new SupplierSpec("S0003", "Nimbus Logistics LLP",       "07LMNOP9012Q1R3", "LMNOP9012Q", RegistrationStatus.Submitted),
        new SupplierSpec("S0004", "Crescent Chemicals",         "33STUVW3456X1Y7", "STUVW3456X", RegistrationStatus.Approved),
        new SupplierSpec("S0005", "Zenith Components Co",       "19BCDEF7890G1H2", "BCDEF7890G", RegistrationStatus.UnderReview),
    };

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var supplierUsers = await ctx.AppUsers
            .Where(u => u.UserCode == "sup1" || u.UserCode == "sup2")
            .ToListAsync(ct);

        var existing = await ctx.Suppliers.IgnoreQueryFilters().Select(s => s.SupplierCode).ToListAsync(ct);

        foreach (var spec in Specs)
        {
            if (existing.Contains(spec.Code)) continue;

            var supplierId = DeterministicId.From("Supplier", spec.Code);
            var seccodeId = DeterministicId.From("Seccode.G", spec.Code);

            ctx.Seccodes.Add(new Seccode
            {
                Id = seccodeId,
                SeccodeType = SeccodeType.G,
                Name = spec.Code + " group",
                SupplierId = supplierId,
                CreatedBy = "seed",
                CreatedOn = now
            });

            ctx.Suppliers.Add(new SupplierEntity
            {
                Id = supplierId,
                SupplierCode = spec.Code,
                LegalName = spec.LegalName,
                TradeName = spec.LegalName,
                SupplierType = Domain.Enums.SupplierType.Material,
                GstNumber = spec.Gst,
                PanNumber = spec.Pan,
                GstValidated = spec.Status is RegistrationStatus.Approved or RegistrationStatus.Active,
                PanValidated = spec.Status is RegistrationStatus.Approved or RegistrationStatus.Active,
                MsmeValidated = false,
                RegistrationStatus = spec.Status,
                IsActiveSupplier = spec.Status == RegistrationStatus.Active,
                SeccodeId = seccodeId,
                InvitedBy = "seed",
                InvitedAt = now.AddDays(-60),
                ApprovedBy = spec.Status == RegistrationStatus.Active ? "admin1" : null,
                ApprovedAt = spec.Status == RegistrationStatus.Active ? now.AddDays(-30) : null,
                CreatedBy = "seed",
                CreatedOn = now
            });

            foreach (var u in supplierUsers)
            {
                var secRightId = DeterministicId.From("SecRight.G", $"{spec.Code}|{u.UserCode}");
                ctx.SecRights.Add(new SecRight
                {
                    Id = secRightId,
                    SeccodeId = seccodeId,
                    UserCode = u.UserCode,
                    CanRead = true,
                    CanWrite = false,
                    CreatedBy = "seed",
                    CreatedOn = now
                });

                ctx.SupplierUserMaps.Add(new SupplierUserMap
                {
                    Id = DeterministicId.From("SupplierUserMap", $"{spec.Code}|{u.UserCode}"),
                    SupplierId = supplierId,
                    AppUserId = u.Id,
                    SecRightId = secRightId,
                    CreatedBy = "seed",
                    CreatedOn = now
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
