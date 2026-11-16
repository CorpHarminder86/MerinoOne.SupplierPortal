using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MerinoOne.SupplierPortal.Tests.Infrastructure.AttachmentGovernanceHarness;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R19 — the report endpoints through the real host on REAL SQL: the two GETs are side-effect-free PREVIEWS served inline, the two
/// POST .../download endpoints return the same PDF as an attachment and (for a SUPPLIER user, when the panel slot is enabled)
/// auto-attach it as a system-generated document, reporting the outcome in <c>X-Report-Attached</c>; and
/// <c>GET api/asns/{id}/invoices</c> lists an ASN's invoices under the caller's row-level security.
///
/// <para>Isolation (the test DB persists between runs and the host runs real background workers): every test builds its own tagged
/// supplier / PO / ASN / invoice and enables the slot with a SUPPLIER-OVERRIDE policy scoped to that fresh supplier, so no other
/// test's panel sees these rows. Each arrange first clears the fixture tenant's policies (the established harness call) so a
/// leftover tenant default from another suite can neither block the ASN post nor "enable" a slot a test means to leave disabled.
/// The real PackingSlip / EInvoice type codes are used (the endpoints hard-code them); an existing but INACTIVE master type or
/// entity makes the test skip rather than flip a shared master.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ReportDownloadAttachTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public ReportDownloadAttachTests(IntegrationTestFixture fx) => _fx = fx;

    private static string PreviewUrl(Guid asnId) => $"/api/asns/{asnId}/packing-list";
    private static string DownloadUrl(Guid asnId) => $"/api/asns/{asnId}/packing-list/download";

    // ═══════════════════════════════════ Packing List ═══════════════════════════════════

    // (a) the GET is a preview: inline, a real PDF, and it writes nothing even though the slot is enabled
    [SkippableFact]
    public async Task Packing_list_get_is_an_inline_preview_with_no_side_effects()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var asn = await NewPrintableAsnAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.PackingSlip, asn.Setup.Supplier.SupplierId);

        var resp = await asn.Supplier.GetAsync(PreviewUrl(asn.AsnId));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var disposition = resp.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("inline");
        $"{disposition.FileName}{disposition.FileNameStar}".Should().Contain($"PackingList-{asn.AsnNumber}.pdf");
        IsPdf(await resp.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        resp.Headers.Contains("X-Report-Attached").Should().BeFalse(because: "a preview never attaches, so it reports nothing");

        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asn.AsnId)).Should().BeEmpty(because: "a preview writes no document");
    }

    // (b) the POST download by a supplier with the slot enabled attaches one system-generated PackingSlip
    [SkippableFact]
    public async Task Packing_list_download_by_a_supplier_attaches_a_system_generated_packing_slip()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var asn = await NewPrintableAsnAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.PackingSlip, asn.Setup.Supplier.SupplierId);

        var resp = await asn.Supplier.PostAsync(DownloadUrl(asn.AsnId), content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var disposition = resp.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("attachment");
        $"{disposition.FileName}{disposition.FileNameStar}".Should().Contain($"PackingList-{asn.AsnNumber}.pdf");
        IsPdf(await resp.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        Attached(resp).Should().Be("true");

        var doc = (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asn.AsnId))
            .Should().ContainSingle().Subject;
        doc.IsSystemGenerated.Should().BeTrue();
        doc.IsDeleted.Should().BeFalse();
        doc.DocumentType.Should().Be(GeneratedReportTypes.PackingSlip);
        doc.FileName.Should().Be($"PackingList-{asn.AsnNumber}.pdf");
        doc.MimeType.Should().Be("application/pdf");
    }

    // (c) a second download REPLACES the first: still exactly one live system document
    [SkippableFact]
    public async Task A_second_packing_list_download_replaces_the_first_attachment()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var asn = await NewPrintableAsnAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.PackingSlip, asn.Setup.Supplier.SupplierId);

        (await asn.Supplier.PostAsync(DownloadUrl(asn.AsnId), content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await asn.Supplier.PostAsync(DownloadUrl(asn.AsnId), content: null);
        second.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(second));
        Attached(second).Should().Be("true");

        var rows = await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asn.AsnId);
        rows.Where(r => r.IsSystemGenerated && !r.IsDeleted).Should().ContainSingle(because: "regenerating replaces, never accumulates");
        rows.Should().HaveCount(2, because: "the first system document is soft-deleted, not hard-deleted");
    }

    // (h) slot not enabled (and, separately, an internal user) ⇒ the download still works, nothing is attached
    [SkippableFact]
    public async Task Packing_list_download_still_works_but_attaches_nothing_when_the_slot_is_not_enabled()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var asn = await NewPrintableAsnAsync();   // no policy seeded for this supplier (the arrange cleared the tenant's)

        var resp = await asn.Supplier.PostAsync(DownloadUrl(asn.AsnId), content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        resp.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        IsPdf(await resp.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        Attached(resp).Should().Be("false");
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asn.AsnId)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Packing_list_download_by_an_internal_user_never_attaches_even_when_the_slot_is_enabled()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var asn = await NewPrintableAsnAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.PackingSlip, asn.Setup.Supplier.SupplierId);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await admin.PostAsync(DownloadUrl(asn.AsnId), content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        Attached(resp).Should().Be("false", because: "only a supplier user's download attaches");
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, asn.AsnId)).Should().BeEmpty();
    }

    // ═══════════════════════════════════ Supplier Invoice report ═══════════════════════════════════

    // (d) the GET is an inline preview of the new report
    [SkippableFact]
    public async Task Invoice_pdf_get_is_an_inline_preview_with_no_side_effects()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var inv = await NewDraftInvoiceAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Invoice, GeneratedReportTypes.EInvoice, inv.Setup.Supplier.SupplierId);

        var resp = await inv.Supplier.GetAsync($"/api/invoices/{inv.InvoiceId}/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var disposition = resp.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("inline");
        $"{disposition.FileName}{disposition.FileNameStar}".Should().Contain($"Invoice-{inv.InvoiceNumber}.pdf");
        IsPdf(await resp.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, inv.InvoiceId)).Should().BeEmpty();
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, inv.AsnId)).Should().BeEmpty();
    }

    // (e) attachTo=invoice (the default) lands on the invoice; attachTo=asn lands on the ASN
    [SkippableFact]
    public async Task Invoice_download_attaches_to_the_invoice_by_default_and_to_the_asn_on_request()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var inv = await NewDraftInvoiceAsync();
        await EnableSlotAsync(DocumentOwnerTypes.Invoice, GeneratedReportTypes.EInvoice, inv.Setup.Supplier.SupplierId);
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.EInvoice, inv.Setup.Supplier.SupplierId);
        var url = $"/api/invoices/{inv.InvoiceId}/pdf/download";

        var toInvoice = await inv.Supplier.PostAsync(url, content: null);
        toInvoice.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(toInvoice));
        var disposition = toInvoice.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("attachment");
        $"{disposition.FileName}{disposition.FileNameStar}".Should().Contain($"Invoice-{inv.InvoiceNumber}.pdf");
        IsPdf(await toInvoice.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        Attached(toInvoice).Should().Be("true");

        var invoiceDoc = (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, inv.InvoiceId)).Should().ContainSingle().Subject;
        invoiceDoc.IsSystemGenerated.Should().BeTrue();
        invoiceDoc.DocumentType.Should().Be(GeneratedReportTypes.EInvoice);
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, inv.AsnId)).Should().BeEmpty(because: "the default target is the invoice");

        var toAsn = await inv.Supplier.PostAsync($"{url}?attachTo=ASN", content: null);   // case-insensitive
        toAsn.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(toAsn));
        Attached(toAsn).Should().Be("true");

        var asnDoc = (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Asn, inv.AsnId)).Should().ContainSingle().Subject;
        asnDoc.IsSystemGenerated.Should().BeTrue();
        asnDoc.IsDeleted.Should().BeFalse();
        asnDoc.DocumentType.Should().Be(GeneratedReportTypes.EInvoice);
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, inv.InvoiceId)).Should().ContainSingle(because: "the ASN download left the invoice's file alone");
    }

    // (f) bad attachTo values are 400
    [SkippableFact]
    public async Task Invoice_download_rejects_an_unknown_attach_target_and_attach_to_asn_without_an_asn()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.ClearPoliciesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync($"rd{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier);
        var invoiceId = await _fx.CreateBareInvoiceAsync(supplier, $"INV-RD-{tag}", InvoiceStatus.Draft);   // AsnId is null
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var bogus = await client.PostAsync($"/api/invoices/{invoiceId}/pdf/download?attachTo=bogus", content: null);
        bogus.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(bogus));
        (await Errors(bogus)).Should().ContainSingle().Which.Should().Contain("attachTo");

        var noAsn = await client.PostAsync($"/api/invoices/{invoiceId}/pdf/download?attachTo=asn", content: null);
        noAsn.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(noAsn));
        (await Errors(noAsn)).Should().ContainSingle().Which.Should().Contain("no ASN");

        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, invoiceId)).Should().BeEmpty();
    }

    // (h) invoice twin: slot not enabled ⇒ 200 + false
    [SkippableFact]
    public async Task Invoice_download_still_works_but_attaches_nothing_when_the_slot_is_not_enabled()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var inv = await NewDraftInvoiceAsync();   // no policy seeded for this supplier

        var resp = await inv.Supplier.PostAsync($"/api/invoices/{inv.InvoiceId}/pdf/download", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        IsPdf(await resp.Content.ReadAsByteArrayAsync()).Should().BeTrue();
        Attached(resp).Should().Be("false");
        (await _fx.LoadUploadsAsync(DocumentOwnerTypes.Invoice, inv.InvoiceId)).Should().BeEmpty();
    }

    // ═══════════════════════════════════ ASN invoice list ═══════════════════════════════════

    // (g) the list: the ASN's non-deleted invoices by date then number; another supplier's user sees none
    [SkippableFact]
    public async Task Asn_invoice_list_returns_the_asns_invoices_in_order_and_is_empty_to_another_suppliers_user()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var today = DateTime.UtcNow.Date;

        var owner = await _fx.CreateSupplierAsync($"al{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier);
        var asnId = await _fx.CreateBareAsnAsync(owner, $"ASN-AL-{tag}", AsnStatus.Submitted);
        var later = await InsertInvoiceAsync(owner, $"INV-AL-B-{tag}", asnId, today);
        var earlier = await InsertInvoiceAsync(owner, $"INV-AL-A-{tag}", asnId, today.AddDays(-3));
        var deleted = await InsertInvoiceAsync(owner, $"INV-AL-X-{tag}", asnId, today.AddDays(-5));
        await SoftDeleteInvoiceAsync(deleted);
        await InsertInvoiceAsync(owner, $"INV-AL-O-{tag}", asnId: null, today.AddDays(-4));   // same supplier, different (no) ASN

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await supplier.GetAsync($"/api/asns/{asnId}/invoices");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var list = (await Read<List<AsnInvoiceRefDto>>(resp)).Data!;
        list.Select(i => i.Id).Should().Equal(new[] { earlier, later }, because: "oldest invoice date first; deleted and other-ASN invoices excluded");
        list[0].InvoiceNumber.Should().Be($"INV-AL-A-{tag}");
        list[0].InvoiceStatus.Should().Be("Draft");
        list[0].InvoiceDate.Should().Be(today.AddDays(-3));

        // Row-level security: an ASN + invoice of a supplier the Supplier-role user holds NO SecRight on.
        var foreign = await _fx.CreateSupplierAsync($"af{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var foreignAsn = await _fx.CreateBareAsnAsync(foreign, $"ASN-AF-{tag}", AsnStatus.Submitted);
        var foreignInvoice = await InsertInvoiceAsync(foreign, $"INV-AF-{tag}", foreignAsn, today);

        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var control = await admin.GetAsync($"/api/asns/{foreignAsn}/invoices");
        control.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(control));
        (await Read<List<AsnInvoiceRefDto>>(control)).Data!.Select(i => i.Id).Should().Equal(new[] { foreignInvoice },
            because: "control: a privileged caller sees it, so the empty list below is row-level security");

        var stranger = await supplier.GetAsync($"/api/asns/{foreignAsn}/invoices");
        stranger.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(stranger));
        (await Read<List<AsnInvoiceRefDto>>(stranger)).Data.Should().BeEmpty();
    }

    // ═══════════════════════════════════ arrange ═══════════════════════════════════

    private sealed record NewAsn(Guid AsnId, string AsnNumber, ProcureToPayFlow.Setup Setup, HttpClient Supplier);

    private sealed record NewInvoice(
        Guid InvoiceId, string InvoiceNumber, Guid AsnId, ProcureToPayFlow.Setup Setup, HttpClient Supplier);

    /// <summary>A Draft ASN of a supplier needing no buyer confirmation, created through the API — printable straight away.</summary>
    private async Task<NewAsn> NewPrintableAsnAsync()
    {
        await _fx.ClearPoliciesAsync();
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var created = (await Read<AsnDetailDto>(create)).Data!;
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, setup.Supplier.SupplierId, required: false);
        return new NewAsn(created.Id, created.AsnNumber, setup, supplier);
    }

    /// <summary>A Draft invoice generated by posting a fresh ASN (the real approval + post flow), plus the ASN's id and the invoice number.</summary>
    private async Task<NewInvoice> NewDraftInvoiceAsync()
    {
        await _fx.ClearPoliciesAsync();
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var posted = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(posted));
        var invoiceId = (await Read<AsnDetailDto>(posted)).Data!.DraftInvoiceId!.Value;

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var number = await db.Invoices.IgnoreQueryFilters().Where(i => i.Id == invoiceId).Select(i => i.InvoiceNumber).SingleAsync();
        return new NewInvoice(invoiceId, number, asnId, setup, client);
    }

    /// <summary>
    /// Enables the (owner entity, real report type) slot for one fresh supplier with a SUPPLIER-OVERRIDE policy. Skips — never
    /// flips — when the shared master type or entity already exists but is inactive.
    /// </summary>
    private async Task EnableSlotAsync(string entityCode, string typeCode, Guid supplierId)
    {
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var type = await db.AttachmentTypes.IgnoreQueryFilters()
                .Where(t => t.TenantId == IntegrationTestFixture.TenantId && t.Code == typeCode && !t.IsDeleted)
                .Select(t => (bool?)t.IsActive).FirstOrDefaultAsync();
            var entity = await db.AttachmentEntities.IgnoreQueryFilters()
                .Where(e => e.TenantId == IntegrationTestFixture.TenantId && e.Code == entityCode && !e.IsDeleted)
                .Select(e => (bool?)e.IsActive).FirstOrDefaultAsync();
            Skip.If(type == false, $"the shared attachment type '{typeCode}' is inactive in the test tenant; this test will not flip it");
            Skip.If(entity == false, $"the shared attachment entity '{entityCode}' is inactive in the test tenant; this test will not flip it");
        }

        await _fx.SeedPoliciesAsync(new PolicySpec(entityCode, typeCode, AttachmentRequirement.Optional, supplierId));
    }

    private async Task<Guid> InsertInvoiceAsync(SecurityTestHarness.SeededSupplier supplier, string number, Guid? asnId, DateTime date)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inv = new Invoice
        {
            Id = Guid.NewGuid(), InvoiceNumber = number, SupplierId = supplier.SupplierId, AsnId = asnId,
            InvoiceDate = date, InvoiceStatus = InvoiceStatus.Draft,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return inv.Id;
    }

    private async Task SoftDeleteInvoiceAsync(Guid invoiceId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Invoices.IgnoreQueryFilters().Where(i => i.Id == invoiceId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDeleted, true));
    }

    // ═══════════════════════════════════ read helpers ═══════════════════════════════════

    private static bool IsPdf(byte[] bytes) => bytes.Length > 1000 && Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF";

    private static string Attached(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("X-Report-Attached", out var values) ? values.Single() : "<missing>";

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<List<string>> Errors(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result>(stream, Json))!.Errors;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
