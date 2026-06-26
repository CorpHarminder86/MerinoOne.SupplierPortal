using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §3.6 / §3.8 "Catalogue migration". Proves the authenticated upload-type validation uses the
/// ACTIVE doc.AttachmentType master (not the legacy DocumentType enum):
/// <list type="bullet">
///   <item>uploading a documentType that is an active master code NOT in the legacy enum → SUCCEEDS (stored as the
///         code);</item>
///   <item>uploading a documentType that is inactive / absent in the master → REJECTED.</item>
/// </list>
/// Uses the Staging owner mode (no owner-existence dependency); canWrite-gated against a fresh supplier's seccode.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AttachmentTypeCatalogueTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AttachmentTypeCatalogueTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Active_master_code_not_in_enum_uploads_successfully()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // A code that is NOT a DocumentType enum member but IS an active master type (admin-added).
        const string customCode = "WeighbridgeSlip";
        var supplier = await _fx.CreateSupplierAsync(
            $"cat-{Guid.NewGuid():N}"[..12], IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        await SeedActiveTypeAsync(customCode, isActive: true);

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await AttachStagingAsync(client, supplier.SupplierId, customCode);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<DocumentAttachmentDto>(resp);
        result.Success.Should().BeTrue(because: await Body(resp));

        // And the row stored the custom code (proves validation used the master, not the enum).
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.DocumentUploads.IgnoreQueryFilters()
            .FirstAsync(d => d.Id == result.Data!.Id);
        stored.DocumentType.Should().Be(customCode);
    }

    [SkippableFact]
    public async Task Inactive_or_absent_master_code_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var supplier = await _fx.CreateSupplierAsync(
            $"cat-{Guid.NewGuid():N}"[..12], IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Absent in the master → rejected.
        var absent = await AttachStagingAsync(client, supplier.SupplierId, "NoSuchTypeCode");
        absent.StatusCode.Should().Be(HttpStatusCode.OK);   // controller returns Result.Fail (200 body, Success=false)
        (await Read<DocumentAttachmentDto>(absent)).Success.Should().BeFalse(because: "absent master code is rejected");

        // INACTIVE master code → also rejected.
        const string inactiveCode = "DeactivatedType";
        await SeedActiveTypeAsync(inactiveCode, isActive: false);
        var inactive = await AttachStagingAsync(client, supplier.SupplierId, inactiveCode);
        (await Read<DocumentAttachmentDto>(inactive)).Success.Should().BeFalse(because: "inactive master code is rejected");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private async Task SeedActiveTypeAsync(string code, bool isActive)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var existing = await db.AttachmentTypes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == IntegrationTestFixture.TenantId && t.Code == code);
        if (existing is null)
            db.AttachmentTypes.Add(new AttachmentType
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, SeccodeId = IntegrationTestFixture.SeccodeId,
                Code = code, Name = code, IsActive = isActive, CreatedBy = "seed", CreatedOn = now,
            });
        else { existing.IsActive = isActive; existing.UpdatedBy = "seed"; existing.UpdatedOn = now; }
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> AttachStagingAsync(HttpClient client, Guid supplierId, string documentType)
    {
        using var form = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes($"%PDF-1.4 {Guid.NewGuid():N}");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "doc.pdf");
        form.Add(new StringContent("Staging"), "ownerEntityType");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "ownerEntityId");   // client-draft staging key
        form.Add(new StringContent(supplierId.ToString()), "supplierId");
        form.Add(new StringContent(documentType), "documentType");
        return await client.PostAsync("/api/document-uploads/attach", form);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
