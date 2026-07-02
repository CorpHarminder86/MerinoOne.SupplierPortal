using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R6 — (l) <c>GET /api/invoices/{id}/pdf</c> renders the frozen snapshot (QuestPDF, Community license) through
/// the normal seccode-scoped query path; (m, plan D16) the <c>/api/document-uploads/attach</c> +
/// <c>DELETE</c> endpoints accept <c>ownerEntityType=Invoice</c> for DRAFT invoices only (mirroring the ASN
/// Draft-only rule — the upload/delete is refused once the invoice is Submitted).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoicePdfAndAttachmentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoicePdfAndAttachmentTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (l) PDF: 200, application/pdf, %PDF magic bytes, non-trivial length ────────────────────────────
    [SkippableFact]
    public async Task Invoice_pdf_returns_a_real_pdf()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftId, _) = await CreateDraftInvoiceAsync();

        var resp = await client.GetAsync($"/api/invoices/{draftId}/pdf");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(1000, because: "a rendered A4 invoice is never a stub");
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF", because: "the payload is a real PDF document");
    }

    // ── (m) invoice attach: Draft OK → delete OK; non-Draft refused (upload AND delete) ────────────────
    [SkippableFact]
    public async Task Invoice_attachments_are_draft_only()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftId, setup) = await CreateDraftInvoiceAsync();

        // Upload to the DRAFT invoice — allowed; documentType defaults to 'Invoice'.
        var upload = await AttachAsync(client, draftId, setup.Supplier.SupplierId);
        upload.Success.Should().BeTrue(because: string.Join("; ", upload.Errors));
        var docId = upload.Data!.Id;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var doc = await db.DocumentUploads.IgnoreQueryFilters().FirstAsync(d => d.Id == docId);
            doc.OwnerEntityType.Should().Be("Invoice");
            doc.OwnerEntityId.Should().Be(draftId);
            doc.DocumentType.Should().Be("Invoice", because: "the Invoice owner-mode default documentType");
            doc.SeccodeId.Should().Be(setup.Supplier.SeccodeId, because: "stamped from the invoice's supplier");
        }

        // Delete on the Draft — allowed (soft-delete).
        var delResp = await client.DeleteAsync($"/api/document-uploads/{docId}");
        delResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(delResp));
        (await ReadPlain(delResp)).Success.Should().BeTrue();

        // A second upload, then SUBMIT the invoice — both attach and delete are now refused.
        var second = await AttachAsync(client, draftId, setup.Supplier.SupplierId);
        second.Success.Should().BeTrue(because: string.Join("; ", second.Errors));

        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftId}",
            new UpdateInvoiceRequest($"INV-ATT-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));

        var lateUpload = await AttachAsync(client, draftId, setup.Supplier.SupplierId);
        lateUpload.Success.Should().BeFalse(because: "attachments are locked once the invoice is Submitted");
        lateUpload.Errors.Should().Contain(e => e.Contains("not Draft", StringComparison.OrdinalIgnoreCase));

        var lateDelete = await client.DeleteAsync($"/api/document-uploads/{second.Data!.Id}");
        (await ReadPlain(lateDelete)).Success.Should().BeFalse(
            because: "deleting an attachment of a Submitted invoice is refused (mirrors the ASN rule)");
    }

    // ── unknown owner type message lists Invoice among the allowed set ─────────────────────────────────
    [SkippableFact]
    public async Task Attach_rejection_message_lists_invoice_owner_type()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftId, setup) = await CreateDraftInvoiceAsync();
        var result = await AttachAsync(client, draftId, setup.Supplier.SupplierId, ownerEntityType: "Bogus");
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("'Invoice'"),
            because: "the allowed owner-type list names Invoice after D16");
    }

    // -------------------- helpers --------------------

    private async Task<(HttpClient Client, Guid DraftInvoiceId, ProcureToPayFlow.Setup Setup)> CreateDraftInvoiceAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var draftInvoiceId = (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceId!.Value;
        return (client, draftInvoiceId, setup);
    }

    private static async Task<Result<DocumentAttachmentDto>> AttachAsync(
        HttpClient client, Guid invoiceId, Guid supplierId, string ownerEntityType = "Invoice")
    {
        using var form = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes($"%PDF-1.4 {Guid.NewGuid():N}");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "tax-invoice.pdf");
        form.Add(new StringContent(ownerEntityType), "ownerEntityType");
        form.Add(new StringContent(invoiceId.ToString()), "ownerEntityId");
        form.Add(new StringContent(supplierId.ToString()), "supplierId");
        var resp = await client.PostAsync("/api/document-uploads/attach", form);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        return await Read<DocumentAttachmentDto>(resp);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<Result> ReadPlain(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
