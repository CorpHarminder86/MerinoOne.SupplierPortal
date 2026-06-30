using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Draft-invoice edit + submit lifecycle through the REAL host. The draft invoice is the one auto-created when
/// the supplier submits an ASN; its number is the placeholder "INV-DRAFT-{asnNumber}". Editing it with a real
/// number + date (PUT) then submitting (/submit) advances it to Submitted; submitting while it still carries
/// the placeholder number is a 400 (the supplier must replace the placeholder first).
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO/ASN per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoiceLifecycleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoiceLifecycleTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Edit_draft_invoice_then_submit_advances_to_submitted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, tag) = await CreateDraftInvoiceAsync();

        // PUT a real invoice number + date.
        var realNumber = $"INV-REAL-{tag}";
        var put = new UpdateInvoiceRequest(realNumber, DateTime.UtcNow.Date, null, null, null, "edited");
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        (await Read<InvoiceDetailDto>(putResp)).Data!.InvoiceNumber.Should().Be(realNumber);

        // Submit → Submitted.
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<InvoiceDetailDto>(submitResp);
        submitted.Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Submitted));
        submitted.Data!.InvoiceNumber.Should().Be(realNumber);
        submitted.Data!.SubmittedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Submitting_draft_invoice_with_placeholder_number_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, _) = await CreateDraftInvoiceAsync();

        // No PUT — the invoice still has the "INV-DRAFT-…" placeholder, so submit must be rejected.
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the draft placeholder number must be replaced with a real number before submit");
        (await Read<InvoiceDetailDto>(submitResp)).Errors.Should().Contain(e => e.Contains("invoice number", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------- setup --------------------

    /// <summary>Seeds a PO, creates + submits an ASN as the supplier, and returns the auto-created draft invoice id.</summary>
    private async Task<(HttpClient Client, Guid DraftInvoiceId, string Tag)> CreateDraftInvoiceAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // R5 — submit via Send-for-Approval → buyer Approve (the auto-created draft invoice surfaces at submit).
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var draftInvoiceId = (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceId!.Value;

        return (client, draftInvoiceId, setup.Tag);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
