using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MerinoOne.SupplierPortal.Tests.Infrastructure.AttachmentGovernanceHarness;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R19 — <see cref="GeneratedReportAttacher"/> on REAL SQL through the real host: a SUPPLIER's download of a generated
/// report (Packing List / Supplier Invoice) is auto-attached to the owner's attachment panel, but only when the panel would
/// render that slot, and each regeneration REPLACES the earlier system-generated file while a user's manual upload of the
/// same type is never touched. The owner's lock state is deliberately ignored.
///
/// <para>Isolation (the test DB persists between runs and the host runs real background workers): every test builds its own
/// tagged supplier / ASN / invoice and a FRESH attachment-type code, and seeds the policy as a SUPPLIER OVERRIDE scoped to
/// that fresh supplier — so no shared AttachmentType is toggled and no other test's panel (tenant default tier) can ever see
/// these rows, now or on the next run. The attacher runs in its own DI scope under a faked supplier principal, the way
/// <c>PackingListTests</c> drives a query through the mediator.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class GeneratedReportAttacherTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4 generated report test payload");

    private readonly IntegrationTestFixture _fx;
    public GeneratedReportAttacherTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (a) enabled + supplier user ⇒ attached, flagged system-generated, shown under the panel slot ──────
    [SkippableFact]
    public async Task A_supplier_download_attaches_the_report_into_the_enabled_panel_slot()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (supplier, tag, type) = await ArrangeEnabledAsync(DocumentOwnerTypes.Asn);
        var asnId = await _fx.CreateBareAsnAsync(supplier, $"ASN-GR-{tag}", AsnStatus.Draft);

        var attached = await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, $"PackingList-{tag}.pdf");

        attached.Should().BeTrue();
        var rows = await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asnId);
        var row = rows.Should().ContainSingle().Subject;
        row.IsSystemGenerated.Should().BeTrue();
        row.IsDeleted.Should().BeFalse();
        row.DocumentType.Should().Be(type);
        row.MimeType.Should().Be("application/pdf");
        row.FileName.Should().Be($"PackingList-{tag}.pdf");
        row.FileUrl.Should().NotBeNullOrWhiteSpace(because: "the bytes went through IFileStorageService");
        row.FileSizeKb.Should().Be((long)Math.Ceiling(Pdf.Length / 1024d));
        row.UploadedBy.Should().Be(SecurityTestHarness.Users.Supplier);
        row.SeccodeId.Should().Be(supplier.SeccodeId, because: "stamped with the supplier's G-seccode so RLS shows it to the supplier");
        row.TenantId.Should().Be(IntegrationTestFixture.TenantId);
        row.AiValidationStatus.Should().Be(AiValidationStatus.Pending);

        var slots = await PanelAsync(DocumentOwnerTypes.Asn, asnId);
        var slot = slots.Should().ContainSingle(s => s.TypeCode == type).Subject;
        slot.Documents.Should().ContainSingle().Which.FileName.Should().Be($"PackingList-{tag}.pdf");
    }

    [SkippableFact]
    public async Task A_supplier_download_attaches_an_invoice_report_the_same_way()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (supplier, tag, type) = await ArrangeEnabledAsync(DocumentOwnerTypes.Invoice);
        var invoiceId = await _fx.CreateBareInvoiceAsync(supplier, $"INV-GR-{tag}", InvoiceStatus.Draft);

        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Invoice, invoiceId, type, $"Invoice-{tag}.pdf")).Should().BeTrue();

        var row = (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, invoiceId)).Should().ContainSingle().Subject;
        row.IsSystemGenerated.Should().BeTrue();
        row.OwnerEntityType.Should().Be(DocumentOwnerTypes.Invoice);
    }

    // ── (b) no policy for the slot ⇒ nothing attached ────────────────────────────────────────────────────
    [SkippableFact]
    public async Task No_policy_for_the_slot_attaches_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"GR{tag}";
        var supplier = await _fx.CreateSupplierAsync($"gr{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier);
        var other = await _fx.CreateSupplierAsync($"go{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var asnId = await _fx.CreateBareAsnAsync(supplier, $"ASN-GN-{tag}", AsnStatus.Draft);

        // The type + entity masters exist, and the only policy is ANOTHER supplier's override — not this owner's slot.
        await _fx.SeedPoliciesAsync(new PolicySpec(DocumentOwnerTypes.Asn, type, AttachmentRequirement.Optional, other.SupplierId));

        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, "r.pdf")).Should().BeFalse();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asnId)).Should().BeEmpty();

        // An unknown type code (no master, no policy) behaves the same — and never throws.
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, $"NOPE{tag}", "r.pdf")).Should().BeFalse();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asnId)).Should().BeEmpty();
    }

    // ── (c) inactive type ⇒ nothing attached ─────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task An_inactive_attachment_type_attaches_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (supplier, tag, type) = await ArrangeEnabledAsync(DocumentOwnerTypes.Asn);
        var asnId = await _fx.CreateBareAsnAsync(supplier, $"ASN-GI-{tag}", AsnStatus.Draft);

        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, "r.pdf")).Should().BeTrue("control: the same setup attaches while the type is active");

        await _fx.SetAttachmentTypeActiveAsync(type, isActive: false);
        var secondOwner = await _fx.CreateBareAsnAsync(supplier, $"ASN-GJ-{tag}", AsnStatus.Draft);
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, secondOwner, type, "r.pdf")).Should().BeFalse();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, secondOwner)).Should().BeEmpty();
    }

    // ── (d) internal users never attach (the download still happens elsewhere) ───────────────────────────
    [SkippableFact]
    public async Task Internal_users_and_other_suppliers_users_never_attach()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"GR{tag}";
        // The BUYER holds a SecRight on this supplier (so a rights-only check would admit them); the Supplier-role user holds none.
        var supplier = await _fx.CreateSupplierAsync($"gb{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Buyer);
        await _fx.SeedPoliciesAsync(new PolicySpec(DocumentOwnerTypes.Asn, type, AttachmentRequirement.Optional, supplier.SupplierId));
        var asnId = await _fx.CreateBareAsnAsync(supplier, $"ASN-GB-{tag}", AsnStatus.Draft);

        (await AttachAsync(SecurityTestHarness.Users.Buyer, RoleNames.Buyer,
            DocumentOwnerTypes.Asn, asnId, type, "r.pdf")).Should().BeFalse(because: "a buyer is an internal user");
        (await AttachAsync(SecurityTestHarness.Users.Admin, RoleNames.Admin,
            DocumentOwnerTypes.Asn, asnId, type, "r.pdf")).Should().BeFalse(because: "an admin is an internal user");
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, "r.pdf")).Should().BeFalse(because: "a supplier user of ANOTHER supplier must not attach here");
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asnId)).Should().BeEmpty();
    }

    // ── (e) a second attach REPLACES the system-generated doc; a manual upload of the same type survives ──
    [SkippableFact]
    public async Task A_second_attach_replaces_the_system_generated_file_and_leaves_a_manual_upload_alone()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (supplier, tag, type) = await ArrangeEnabledAsync(DocumentOwnerTypes.Asn);
        var asnId = await _fx.CreateBareAsnAsync(supplier, $"ASN-GR2-{tag}", AsnStatus.Draft);
        var manualId = await _fx.AddUploadAsync(DocumentOwnerTypes.Asn, asnId, type, supplier.SeccodeId, isSystemGenerated: false);

        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, "first.pdf")).Should().BeTrue();
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, asnId, type, "second.pdf")).Should().BeTrue();

        var rows = await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asnId);
        rows.Should().HaveCount(3, because: "manual + first (now deleted) + second");

        var manual = rows.Single(r => r.Id == manualId);
        manual.IsDeleted.Should().BeFalse(because: "a user's manual upload is never replaced");
        manual.IsSystemGenerated.Should().BeFalse();

        var generated = rows.Where(r => r.IsSystemGenerated).ToList();
        generated.Should().HaveCount(2);
        generated.Single(r => r.FileName == "first.pdf").IsDeleted.Should().BeTrue();
        generated.Single(r => r.FileName == "second.pdf").IsDeleted.Should().BeFalse();

        var slot = (await PanelAsync(DocumentOwnerTypes.Asn, asnId)).Single(s => s.TypeCode == type);
        slot.Documents.Select(d => d.FileName).Should().BeEquivalentTo(new[] { manual.FileName, "second.pdf" });
    }

    // ── (f) the owner's lock state is ignored ────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_posted_asn_and_a_submitted_invoice_still_get_the_report_attached()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (asnSupplier, tag, asnType) = await ArrangeEnabledAsync(DocumentOwnerTypes.Asn);
        var postedAsn = await _fx.CreateBareAsnAsync(asnSupplier, $"ASN-GL-{tag}", AsnStatus.Submitted);
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, postedAsn, asnType, "posted.pdf")).Should().BeTrue(because: "ASN attachments lock once posted, the generated report does not");

        var (invSupplier, tag2, invType) = await ArrangeEnabledAsync(DocumentOwnerTypes.Invoice);
        var submitted = await _fx.CreateBareInvoiceAsync(invSupplier, $"INV-GL-{tag2}", InvoiceStatus.Submitted);
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Invoice, submitted, invType, "submitted.pdf")).Should().BeTrue(because: "invoice attachments lock once submitted, the generated report does not");

        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, postedAsn)).Should().ContainSingle().Which.IsSystemGenerated.Should().BeTrue();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, submitted)).Should().ContainSingle().Which.IsSystemGenerated.Should().BeTrue();
    }

    [SkippableFact]
    public async Task An_unknown_owner_type_or_missing_owner_returns_false_without_throwing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (_, _, type) = await ArrangeEnabledAsync(DocumentOwnerTypes.Asn);

        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            DocumentOwnerTypes.Asn, Guid.NewGuid(), type, "r.pdf")).Should().BeFalse(because: "no such ASN");
        (await AttachAsync(SecurityTestHarness.Users.Supplier, RoleNames.Supplier,
            "PurchaseOrder", Guid.NewGuid(), type, "r.pdf")).Should().BeFalse(because: "not an ASN / Invoice owner");
    }

    // ═══════════════════════════════════ arrange / act / read ═══════════════════════════════════

    /// <summary>
    /// A fresh supplier (the Supplier-role test user holds a SecRight on it) plus a fresh attachment type enabled for it through
    /// a supplier-override policy on <paramref name="entityCode"/> — the exact slot the panel renders for that supplier's owner.
    /// </summary>
    private async Task<(SecurityTestHarness.SeededSupplier Supplier, string Tag, string Type)> ArrangeEnabledAsync(string entityCode)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"GR{tag}";
        var supplier = await _fx.CreateSupplierAsync($"gr{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier);
        await _fx.SeedPoliciesAsync(new PolicySpec(entityCode, type, AttachmentRequirement.Optional, supplier.SupplierId));
        return (supplier, tag, type);
    }

    /// <summary>
    /// Runs the attacher in its own DI scope under a faked principal (userCode + role + tenant claims on the HTTP-context
    /// accessor — async-local, so it ends with this call), exactly as <c>PackingListTests</c> drives a query through the mediator.
    /// </summary>
    private async Task<bool> AttachAsync(string userCode, string role, string ownerType, Guid ownerId, string documentType, string fileName)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("userCode", userCode),
                new Claim(ClaimTypes.Role, role),
                new Claim("tenant", IntegrationTestFixture.TenantId.ToString()),
            }, authenticationType: "test")),
        };
        http.Request.Headers["X-Active-Company"] = IntegrationTestFixture.CompanyId.ToString();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        return await scope.ServiceProvider.GetRequiredService<GeneratedReportAttacher>()
            .AttachAsync(ownerType, ownerId, documentType, fileName, Pdf, CancellationToken.None);
    }

    private async Task<List<AttachmentPanelSlotDto>> PanelAsync(string entity, Guid id)
    {
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await client.GetAsync($"/api/attachments/panel?entity={entity}&id={id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var stream = await resp.Content.ReadAsStreamAsync();
        var result = (await JsonSerializer.DeserializeAsync<Result<List<AttachmentPanelSlotDto>>>(stream, Json))!;
        result.Success.Should().BeTrue();
        return result.Data!;
    }
}
