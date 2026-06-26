using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// R4 (2026-06-26) — Phase 4 (Attachment Requirement Governance) test-data builders, layered ON TOP of the
/// fixture's money-path seed (never mutating it). Seeds the attachment masters + policy rows for the FIXTURE
/// tenant (the production AttachmentGovernanceSeeder seeds the production seed-tenant, a different tenant), and
/// inserts DocumentUpload rows against an instance to simulate "attachment present".
///
/// <para>Masters/policies are owned by the fixture supplier's G-seccode + the fixture tenant/company. The money
/// path runs with the scope gate OFF so the supplier/admin API clients read these without RLS interference; the
/// evaluator itself reads the policy filter-bypassed (the policy is tenant config, not supplier-scoped).</para>
/// </summary>
public static class AttachmentGovernanceHarness
{
    /// <summary>One requirement to seed: (entity code, type code/name, requirement, supplier override?).</summary>
    public sealed record PolicySpec(
        string EntityCode, string TypeCode, AttachmentRequirement Requirement, Guid? SupplierId = null);

    /// <summary>
    /// Ensures the attachment-type + entity masters exist for the given codes (fixture tenant), then upserts each
    /// PolicySpec as an active AttachmentRequirementPolicy (tenant default when SupplierId is null, else a supplier
    /// override). Idempotent by (tenant, entity, type, supplier). Returns nothing — the caller seeds, then submits.
    /// </summary>
    public static async Task SeedPoliciesAsync(this IntegrationTestFixture fx, params PolicySpec[] specs)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tenantId = IntegrationTestFixture.TenantId;
        var companyId = IntegrationTestFixture.CompanyId;
        var seccodeId = IntegrationTestFixture.SeccodeId;

        // Ensure entities.
        foreach (var entityCode in specs.Select(s => s.EntityCode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = await db.AttachmentEntities.IgnoreQueryFilters()
                .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Code == entityCode);
            if (existing is null)
            {
                db.AttachmentEntities.Add(new AttachmentEntity
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = companyId, SeccodeId = seccodeId,
                    Code = entityCode, Name = entityCode, IsActive = true, CreatedBy = "seed", CreatedOn = now,
                });
            }
        }

        // Ensure types.
        foreach (var typeCode in specs.Select(s => s.TypeCode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = await db.AttachmentTypes.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Code == typeCode);
            if (existing is null)
            {
                db.AttachmentTypes.Add(new AttachmentType
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = companyId, SeccodeId = seccodeId,
                    Code = typeCode, Name = typeCode, IsActive = true, CreatedBy = "seed", CreatedOn = now,
                });
            }
        }
        await db.SaveChangesAsync();

        var entityIds = await db.AttachmentEntities.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .ToDictionaryAsync(e => e.Code, e => e.Id, StringComparer.OrdinalIgnoreCase);
        var typeIds = await db.AttachmentTypes.IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .ToDictionaryAsync(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            var entityId = entityIds[spec.EntityCode];
            var typeId = typeIds[spec.TypeCode];

            // Only consider NON-deleted rows as "existing" — a prior test soft-deletes via ClearPoliciesAsync, and
            // reviving a soft-deleted row (isDeleted stays 1) would leave it invisible to the !IsDeleted reads.
            var existing = await db.AttachmentRequirementPolicies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                          && p.AttachmentEntityId == entityId
                                          && p.AttachmentTypeId == typeId
                                          && p.SupplierId == spec.SupplierId
                                          && !p.IsDeleted);
            if (existing is null)
            {
                db.AttachmentRequirementPolicies.Add(new AttachmentRequirementPolicy
                {
                    Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = companyId, SeccodeId = seccodeId,
                    AttachmentEntityId = entityId, AttachmentTypeId = typeId, SupplierId = spec.SupplierId,
                    Requirement = spec.Requirement, IsActive = true, CreatedBy = "seed", CreatedOn = now,
                });
            }
            else
            {
                existing.Requirement = spec.Requirement;
                existing.IsActive = true;
                existing.UpdatedBy = "seed";
                existing.UpdatedOn = now;
            }
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes (soft-deletes) every attachment policy for the fixture tenant so a prior test's seeded policy never
    /// leaks into a later test (the suite shares ONE DB; tests are serial). Call at the start of each test that
    /// asserts on a specific policy.
    /// </summary>
    public static async Task ClearPoliciesAsync(this IntegrationTestFixture fx)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.AttachmentRequirementPolicies.IgnoreQueryFilters()
            .Where(p => p.TenantId == IntegrationTestFixture.TenantId && !p.IsDeleted)
            .ToListAsync();
        foreach (var r in rows) db.AttachmentRequirementPolicies.Remove(r);   // interceptor soft-deletes
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a DocumentUpload row tagged to (ownerEntityType, ownerEntityId) with the given attachment-type code,
    /// stamped with the fixture supplier's seccode/tenant/company, simulating "attachment present".
    /// </summary>
    public static async Task<Guid> AddUploadAsync(
        this IntegrationTestFixture fx, string ownerEntityType, Guid ownerEntityId, string typeCode,
        Guid? seccodeId = null)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.DocumentUploads.Add(new DocumentUpload
        {
            Id = id, OwnerEntityType = ownerEntityType, OwnerEntityId = ownerEntityId,
            DocumentType = typeCode, FileName = $"{typeCode}.pdf", FileUrl = $"test/{id:N}.pdf",
            FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed",
            SeccodeId = seccodeId ?? IntegrationTestFixture.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            AiValidationStatus = AiValidationStatus.Pending, CreatedBy = "seed", CreatedOn = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Counts the "Attachment warning skipped" audit rows for an instance (asserts the skip is audited).</summary>
    public static async Task<int> CountWarningSkipAuditAsync(
        this IntegrationTestFixture fx, string entityCode, Guid entityId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(a => a.EntityName == entityCode && a.EntityId == entityId
                             && a.FieldName == "Attachment warning skipped");
    }
}
