using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §3.6/§3.7, Component 5 (Attachment Requirement Governance). Seeds the
/// tenant-scoped attachment masters: <see cref="AttachmentEntity"/> (Supplier / Asn / Invoice) and
/// <see cref="AttachmentType"/> (Invoice, PackingSlip, TestCertificate, EInvoice, EWayBill, Pan, Cheque, Gst,
/// Msme). NO <c>AttachmentRequirementPolicy</c> seed — absence of a policy row = Optional (UC-ATT-06).
///
/// <para>Runs AFTER <see cref="TenantSeeder"/> so the tenant + the tenant-admin seccode both exist (these
/// aggregates own a seccode; they're owned by the tenant-admin seccode so the admin can read/write them).
/// Idempotent (deterministic ids, existence-checked per tenant). <c>CreatedBy = "seed"</c> short-circuits the
/// audit interceptor.</para>
///
/// <para>AttachmentType.Code aligns with the existing <c>DocumentType</c> enum member names where they overlap
/// (Invoice / PackingSlip / TestCertificate / EInvoice / EWayBill). Pan / Cheque / Gst / Msme are the
/// onboarding-document codes (the enum spells those OnboardingPan/…; the catalogue uses the short code form).</para>
/// </summary>
public static class AttachmentGovernanceSeeder
{
    private record EntitySpec(string Code, string Name);
    private record TypeSpec(string Code, string Name);

    public static readonly IReadOnlyList<(string Code, string Name)> Entities = new[]
    {
        ("Supplier", "Supplier"),
        ("Asn",      "Advance Shipping Notice"),
        ("Invoice",  "Invoice"),
    };

    public static readonly IReadOnlyList<(string Code, string Name)> Types = new[]
    {
        ("Invoice",         "Invoice"),
        ("PackingSlip",     "Packing Slip"),
        ("TestCertificate", "Test Certificate"),
        ("EInvoice",        "E-Invoice"),
        ("EWayBill",        "E-Way Bill"),
        ("Pan",             "PAN"),
        ("Cheque",          "Cancelled Cheque"),
        ("Gst",             "GST Certificate"),
        ("Msme",            "MSME Certificate"),
    };

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenantId = TenantSeeder.TenantId;
        // The tenant-admin seccode (created by TenantSeeder) owns these tenant-wide config masters so the
        // admin's SecRight grants read/write under the seccode RLS filter.
        var seccodeId = DeterministicId.From("Seccode.U", TenantSeeder.AdminUserCode);

        var existingEntityCodes = await ctx.Set<AttachmentEntity>().IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId).Select(e => e.Code).ToListAsync(ct);
        foreach (var (code, name) in Entities)
        {
            if (existingEntityCodes.Contains(code)) continue;
            ctx.Set<AttachmentEntity>().Add(new AttachmentEntity
            {
                Id = DeterministicId.From("AttachmentEntity", $"{TenantSeeder.TenantName}|{code}"),
                TenantId = tenantId,
                SeccodeId = seccodeId,
                Code = code,
                Name = name,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }

        var existingTypeCodes = await ctx.Set<AttachmentType>().IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId).Select(t => t.Code).ToListAsync(ct);
        foreach (var (code, name) in Types)
        {
            if (existingTypeCodes.Contains(code)) continue;
            ctx.Set<AttachmentType>().Add(new AttachmentType
            {
                Id = DeterministicId.From("AttachmentType", $"{TenantSeeder.TenantName}|{code}"),
                TenantId = tenantId,
                SeccodeId = seccodeId,
                Code = code,
                Name = name,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }

        await ctx.SaveChangesAsync(ct);
    }
}
