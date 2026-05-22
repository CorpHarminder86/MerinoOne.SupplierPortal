using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class MasterSeeder
{
    public record DeliveryTermSpec(string Code, string Description);
    public record PaymentTermSpec(string Code, string Description, int NetDays);
    public record ItemSpec(string Code, string Description, string Uom, string? HsnCode);

    public static readonly IReadOnlyList<DeliveryTermSpec> DeliveryTerms = new[]
    {
        new DeliveryTermSpec("FOB", "Free On Board"),
        new DeliveryTermSpec("CIF", "Cost, Insurance and Freight"),
        new DeliveryTermSpec("DAP", "Delivered At Place"),
        new DeliveryTermSpec("EXW", "Ex Works"),
        new DeliveryTermSpec("DDP", "Delivered Duty Paid"),
        new DeliveryTermSpec("FCA", "Free Carrier"),
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

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // DeliveryTerm
        var existingDeliveryCodes = await ctx.DeliveryTerms
            .IgnoreQueryFilters()
            .Select(d => d.Code).ToListAsync(ct);
        var newDelivery = DeliveryTerms
            .Where(d => !existingDeliveryCodes.Contains(d.Code))
            .Select(d => new DeliveryTerm
            {
                Id = DeterministicId.From("DeliveryTerm", d.Code),
                Code = d.Code,
                Description = d.Description,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newDelivery.Count > 0) ctx.DeliveryTerms.AddRange(newDelivery);

        // PaymentTerm
        var existingPaymentCodes = await ctx.PaymentTerms
            .IgnoreQueryFilters()
            .Select(p => p.Code).ToListAsync(ct);
        var newPayment = PaymentTerms
            .Where(p => !existingPaymentCodes.Contains(p.Code))
            .Select(p => new PaymentTerm
            {
                Id = DeterministicId.From("PaymentTerm", p.Code),
                Code = p.Code,
                Description = p.Description,
                NetDays = p.NetDays,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newPayment.Count > 0) ctx.PaymentTerms.AddRange(newPayment);

        // Item
        var existingItemCodes = await ctx.Items
            .IgnoreQueryFilters()
            .Select(i => i.Code).ToListAsync(ct);
        var newItems = Items
            .Where(i => !existingItemCodes.Contains(i.Code))
            .Select(i => new Item
            {
                Id = DeterministicId.From("Item", i.Code),
                Code = i.Code,
                Description = i.Description,
                Uom = i.Uom,
                HsnCode = i.HsnCode,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            }).ToList();
        if (newItems.Count > 0) ctx.Items.AddRange(newItems);

        await ctx.SaveChangesAsync(ct);
    }
}
