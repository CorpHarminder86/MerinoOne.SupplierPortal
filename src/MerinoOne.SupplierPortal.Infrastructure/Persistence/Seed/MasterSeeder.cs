using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static partial class MasterSeeder
{
    public record DeliveryTermSpec(string Code, string Description);
    public record PaymentTermSpec(string Code, string Description, int NetDays);
    public record ItemSpec(string Code, string Description, string UnitCode, string? HsnCode);

    // The legacy default company the new company-scoped masters (Unit/ItemGroup/Item) are seeded under.
    private static Guid Tenant => TenantSeeder.TenantId;
    private static Guid Company2000 => TenantSeeder.CompanyId("2000");

    public static readonly IReadOnlyList<DeliveryTermSpec> DeliveryTerms = new[]
    {
        new DeliveryTermSpec("FOB", "Free On Board"),
        new DeliveryTermSpec("CIF", "Cost, Insurance and Freight"),
        new DeliveryTermSpec("DAP", "Delivered At Place"),
        new DeliveryTermSpec("EXW", "Ex Works"),
        new DeliveryTermSpec("DDP", "Delivered Duty Paid"),
        new DeliveryTermSpec("FCA", "Free Carrier"),
    };

    // Tax master (R4 Module 6) — company-shared (ICompanyScoped), seeded under source company 2000 (same as
    // Item/ItemGroup). createdBy='seed' short-circuits the audit interceptor.
    public record TaxSpec(string Code, string Description, decimal TaxRate);
    public static readonly IReadOnlyList<TaxSpec> Taxes = new[]
    {
        new TaxSpec("CGST", "Central GST", 9m),
        new TaxSpec("SGST", "State GST", 9m),
        new TaxSpec("IGST", "Integrated GST", 18m),
    };

    public static readonly IReadOnlyList<PaymentTermSpec> PaymentTerms = new[]
    {
        new PaymentTermSpec("NET15", "Net 15 days", 15),
        new PaymentTermSpec("NET30", "Net 30 days", 30),
        new PaymentTermSpec("NET45", "Net 45 days", 45),
        new PaymentTermSpec("NET60", "Net 60 days", 60),
        new PaymentTermSpec("IMMEDIATE", "Immediate payment", 0),
        new PaymentTermSpec("2/10NET30", "2% discount if paid in 10 days, otherwise net 30", 30),
    };

    public static readonly IReadOnlyList<ItemSpec> Items = new[]
    {
        new ItemSpec("ITM-00001", "Polypropylene Granules", "KG",   "39021000"),
        new ItemSpec("ITM-00002", "Steel Plate 10mm",       "KG",   "72085200"),
        new ItemSpec("ITM-00003", "Aluminium Sheet 2mm",    "KG",   "76061200"),
        new ItemSpec("ITM-00004", "Copper Wire 1.5sqmm",    "MTR",  "74081100"),
        new ItemSpec("ITM-00005", "Industrial Solvent",     "LTR",  "29051200"),
        new ItemSpec("ITM-00006", "Hydraulic Oil ISO 68",   "LTR",  "27101983"),
        new ItemSpec("ITM-00007", "PVC Pipe 110mm",         "MTR",  "39172390"),
        new ItemSpec("ITM-00008", "Stainless Steel Bolt M10","NOS", "73181500"),
        new ItemSpec("ITM-00009", "Bearing 6205-ZZ",        "NOS",  "84821010"),
        new ItemSpec("ITM-00010", "Industrial Lubricant",   "LTR",  "34031900"),
        new ItemSpec("ITM-00011", "Rubber Gasket 100mm",    "NOS",  "40169320"),
        new ItemSpec("ITM-00012", "Mild Steel Angle 50x50", "MTR",  "72163100"),
        new ItemSpec("ITM-00013", "Welding Electrode 3.15mm","KG",  "83111000"),
        new ItemSpec("ITM-00014", "Cement OPC 53 Grade",    "BAG",  "25232930"),
        new ItemSpec("ITM-00015", "TMT Steel Bar 12mm",     "KG",   "72142090"),
        new ItemSpec("ITM-00016", "Industrial Paint Primer","LTR",  "32081090"),
        new ItemSpec("ITM-00017", "Cable Tie 200mm",        "NOS",  "39269080"),
        new ItemSpec("ITM-00018", "MCB 32A Single Pole",    "NOS",  "85362020"),
        new ItemSpec("ITM-00019", "LED Industrial Light 50W","NOS", "94054090"),
        new ItemSpec("ITM-00020", "Safety Helmet",          "NOS",  "65061010"),
    };

    // ---- Reference data (tenant-scoped geo/currency) ----
    // CurrencySpec/CountrySpec/StateSpec/CitySpec/PostalSpec record types and the full
    // Currencies / Countries / States / Cities / PostalCodes data sets live in the partial
    // file MasterSeeder.GeoData.cs (ISO 4217 currencies + India states/UTs/cities/PIN codes).

    // ---- Inventory reference (company-scoped, source 2000) ----
    private record UnitSpec(string Code, string Desc, UnitType Type, string Iso, decimal Factor, string? BaseCode);
    private record ItemGroupSpec(string Code, string Desc);

    private static readonly UnitSpec[] Units =
    {
        new("EA",  "Each",      UnitType.Quantity, "EA",  1m,     null),
        new("NOS", "Numbers",   UnitType.Quantity, "NOS", 1m,     null),
        new("KG",  "Kilogram",  UnitType.Mass,     "KGM", 1m,     null),
        new("GRM", "Gram",      UnitType.Mass,     "GRM", 0.001m, "KG"),
        new("MTR", "Metre",     UnitType.Length,   "MTR", 1m,     null),
        new("LTR", "Litre",     UnitType.Volume,   "LTR", 1m,     null),
        new("BAG", "Bag",       UnitType.Quantity, "BG",  1m,     null),
    };
    private static readonly ItemGroupSpec[] ItemGroups =
    {
        new("RAW",  "Raw Material"),
        new("FIN",  "Finished Goods"),
        new("CONS", "Consumables"),
    };

    private static Guid CurId(string c) => DeterministicId.From("Currency", c);
    private static Guid CtryId(string c) => DeterministicId.From("Country", c);
    private static Guid StId(string c) => DeterministicId.From("State", c);
    private static Guid CityId(string c) => DeterministicId.From("City", c);
    private static Guid PinId(string c) => DeterministicId.From("PostalCode", c);
    private static Guid UnitId(string c) => DeterministicId.From("Unit", c);
    private static Guid GroupId(string c) => DeterministicId.From("ItemGroup", c);

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // DeliveryTerm
        var existingDeliveryCodes = await ctx.DeliveryTerms.IgnoreQueryFilters().Select(d => d.Code).ToListAsync(ct);
        var newDelivery = DeliveryTerms.Where(d => !existingDeliveryCodes.Contains(d.Code))
            .Select(d => new DeliveryTerm { Id = DeterministicId.From("DeliveryTerm", d.Code), Code = d.Code, Description = d.Description, IsActive = true, CreatedBy = "seed", CreatedOn = now }).ToList();
        if (newDelivery.Count > 0) ctx.DeliveryTerms.AddRange(newDelivery);

        // PaymentTerm
        var existingPaymentCodes = await ctx.PaymentTerms.IgnoreQueryFilters().Select(p => p.Code).ToListAsync(ct);
        var newPayment = PaymentTerms.Where(p => !existingPaymentCodes.Contains(p.Code))
            .Select(p => new PaymentTerm { Id = DeterministicId.From("PaymentTerm", p.Code), Code = p.Code, Description = p.Description, NetDays = p.NetDays, IsActive = true, CreatedBy = "seed", CreatedOn = now }).ToList();
        if (newPayment.Count > 0) ctx.PaymentTerms.AddRange(newPayment);

        // Tax (R4 Module 6) — company-shared, seeded under source company 2000 (mirrors Item/ItemGroup).
        var existingTaxCodes = await ctx.Taxes.IgnoreQueryFilters().Select(t => t.Code).ToListAsync(ct);
        var newTaxes = Taxes.Where(t => !existingTaxCodes.Contains(t.Code))
            .Select(t => new Tax
            {
                Id = DeterministicId.From("Tax", t.Code),
                TenantId = Tenant,
                TenantEntityId = Company2000,
                Code = t.Code,
                Description = t.Description,
                TaxRate = t.TaxRate,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newTaxes.Count > 0) ctx.Taxes.AddRange(newTaxes);

        await SeedReferenceDataAsync(ctx, now, ct);
        await SeedInventoryReferenceAsync(ctx, now, ct);

        // Item — company-scoped (source 2000), linked to a Unit (by unit code) + the RAW item group.
        var rawGroupId = GroupId("RAW");
        var existingItemCodes = await ctx.Items.IgnoreQueryFilters().Select(i => i.Code).ToListAsync(ct);
        var newItems = Items.Where(i => !existingItemCodes.Contains(i.Code))
            .Select(i => new Item
            {
                Id = DeterministicId.From("Item", i.Code),
                TenantId = Tenant,
                TenantEntityId = Company2000,
                Code = i.Code,
                Description = i.Description,
                HsnCode = i.HsnCode,
                UnitId = UnitId(i.UnitCode),
                ItemGroupId = rawGroupId,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newItems.Count > 0) ctx.Items.AddRange(newItems);

        // Link any pre-existing seed items (rows that predate the Item↔Unit/ItemGroup promotion) so the
        // sample set demonstrates the FK links. Idempotent: only touches rows whose link is still null.
        var seedCodes = Items.Select(i => i.Code).ToList();
        var unitByCode = Items.ToDictionary(i => i.Code, i => i.UnitCode);
        var toLink = await ctx.Items.IgnoreQueryFilters()
            .Where(i => seedCodes.Contains(i.Code) && (i.ItemGroupId == null || i.UnitId == null))
            .ToListAsync(ct);
        foreach (var it in toLink)
        {
            it.ItemGroupId ??= rawGroupId;
            it.UnitId ??= UnitId(unitByCode[it.Code]);
            it.UpdatedBy = "seed";
            it.UpdatedOn = now;
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static async Task SeedReferenceDataAsync(AppDbContext ctx, DateTime now, CancellationToken ct)
    {
        var haveCur = await ctx.Currencies.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.Currencies.AddRange(Currencies.Where(c => !haveCur.Contains(c.Code))
            .Select(c => new Currency { Id = CurId(c.Code), TenantId = Tenant, Code = c.Code, Description = c.Desc, IsoCode = c.Iso, Symbol = c.Symbol, DecimalPlaces = 2, IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        var haveCtry = await ctx.Countries.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.Countries.AddRange(Countries.Where(c => !haveCtry.Contains(c.Code))
            .Select(c => new Country { Id = CtryId(c.Code), TenantId = Tenant, Code = c.Code, Description = c.Desc, IsoCode2 = c.Iso2, IsoCode3 = c.Iso3, TelephoneCode = c.Tel, CurrencyId = CurId(c.CurrencyCode), IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        var haveSt = await ctx.States.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.States.AddRange(States.Where(s => !haveSt.Contains(s.Code))
            .Select(s => new State { Id = StId(s.Code), TenantId = Tenant, Code = s.Code, Description = s.Desc, CountryId = CtryId(s.CountryCode), IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        var haveCity = await ctx.Cities.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.Cities.AddRange(Cities.Where(c => !haveCity.Contains(c.Code))
            .Select(c => new City { Id = CityId(c.Code), TenantId = Tenant, Code = c.Code, Description = c.Desc, CountryId = CtryId(c.CountryCode), StateId = c.StateCode == null ? null : StId(c.StateCode), IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        var havePin = await ctx.PostalCodes.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.PostalCodes.AddRange(PostalCodes.Where(p => !havePin.Contains(p.Code))
            .Select(p => new PostalCode { Id = PinId(p.Code), TenantId = Tenant, Code = p.Code, Area = p.Area, CountryId = CtryId(p.CountryCode), StateId = p.StateCode == null ? null : StId(p.StateCode), CityId = p.CityCode == null ? null : CityId(p.CityCode), IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        await ctx.SaveChangesAsync(ct);   // persist geo FK chain before items reference it
    }

    private static async Task SeedInventoryReferenceAsync(AppDbContext ctx, DateTime now, CancellationToken ct)
    {
        var haveGroups = await ctx.ItemGroups.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.ItemGroups.AddRange(ItemGroups.Where(g => !haveGroups.Contains(g.Code))
            .Select(g => new ItemGroup { Id = GroupId(g.Code), TenantId = Tenant, TenantEntityId = Company2000, Code = g.Code, Description = g.Desc, IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        var haveUnits = await ctx.Units.IgnoreQueryFilters().Select(x => x.Code).ToListAsync(ct);
        ctx.Units.AddRange(Units.Where(u => !haveUnits.Contains(u.Code))
            .Select(u => new Unit { Id = UnitId(u.Code), TenantId = Tenant, TenantEntityId = Company2000, Code = u.Code, Description = u.Desc, UnitType = u.Type, IsoCode = u.Iso, DecimalPlaces = 2, ConversionFactor = u.Factor, BaseUnitId = u.BaseCode == null ? null : UnitId(u.BaseCode), IsActive = true, CreatedBy = "seed", CreatedOn = now }));

        await ctx.SaveChangesAsync(ct);
    }
}
