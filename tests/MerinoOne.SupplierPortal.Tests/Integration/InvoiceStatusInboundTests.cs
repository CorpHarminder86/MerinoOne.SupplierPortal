using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R6 review fixes — the inbound ERP invoice-status writer vs the per-PO-line invoiced-qty reservation (plan D8):
/// (1) an inbound push that moves a reservation-holding invoice (e.g. Matched) TO Rejected must RELEASE the
/// reservation atomically with the status flip; (2) Rejected is TERMINAL for this writer — LN can never re-advance
/// a Rejected invoice (whose reservation was already released), the row is reported failed. Runs through the REAL
/// host on real SQL (the release is a conditional ExecuteUpdate — EF InMemory can't reproduce it).
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO/ASN per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoiceStatusInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoiceStatusInboundTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (1) inbound Rejected on a Matched invoice releases invoicedQtyToDate ───────────────────────────
    [SkippableFact]
    public async Task Inbound_rejected_on_matched_invoice_releases_the_reservation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (invoiceId, invoiceNumber, setup) = await CreateSubmittedMatchedInvoiceAsync("REJREL");
        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty,
            because: "submit reserved the billed qty onto the PO line");

        var result = await PushInvoiceStatusAsync(invoiceNumber, nameof(InvoiceStatus.Rejected));
        result.Failed.Should().Be(0, because: "Matched → Rejected is a legal ERP advance");
        result.Updated.Should().Be(1);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Invoices.IgnoreQueryFilters().Where(i => i.Id == invoiceId)
                .Select(i => i.InvoiceStatus).FirstAsync())
            .Should().Be(InvoiceStatus.Rejected);

        (await InvoicedToDate(setup.PoLineId)).Should().Be(0m,
            because: "leaving the reservation-holding set via an inbound rejection releases the per-PO-line " +
                     "reservation in the same transaction as the status flip (plan D8)");
    }

    // ── (2) LN cannot re-advance a Rejected invoice — the row is reported failed, nothing changes ──────
    [SkippableFact]
    public async Task Inbound_push_cannot_advance_a_rejected_invoice()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (invoiceId, invoiceNumber, setup) = await CreateSubmittedMatchedInvoiceAsync("REJADV");

        // Reject via the inbound path (releases the reservation)…
        (await PushInvoiceStatusAsync(invoiceNumber, nameof(InvoiceStatus.Rejected))).Failed.Should().Be(0);
        (await InvoicedToDate(setup.PoLineId)).Should().Be(0m);

        // …then LN attempts to advance the terminal invoice to Paid — the guard fails the row.
        var advance = await PushInvoiceStatusAsync(invoiceNumber, nameof(InvoiceStatus.Paid));
        advance.Failed.Should().Be(1,
            because: "Rejected is terminal for the inbound writer — its reservation was already released");
        advance.Rows.Should().ContainSingle().Which.Error.Should().Contain("Rejected",
            because: "the failed-row message names the terminal state");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Invoices.IgnoreQueryFilters().Where(i => i.Id == invoiceId)
                .Select(i => i.InvoiceStatus).FirstAsync())
            .Should().Be(InvoiceStatus.Rejected, because: "the failed row changed nothing");
        (await InvoicedToDate(setup.PoLineId)).Should().Be(0m,
            because: "no phantom re-reservation nor a double-release happened");
    }

    // -------------------- helpers --------------------

    /// <summary>PO → ASN (approve-submitted) → auto-draft → PUT real number → submit ⇒ Matched invoice.</summary>
    private async Task<(Guid InvoiceId, string InvoiceNumber, ProcureToPayFlow.Setup Setup)>
        CreateSubmittedMatchedInvoiceAsync(string prefix)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitAsn = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplier, asnId);
        submitAsn.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitAsn));
        var draftId = (await Read<AsnDetailDto>(submitAsn)).Data!.DraftInvoiceId!.Value;

        var invoiceNumber = $"INV-{prefix}-{setup.Tag}";
        var putResp = await supplier.PutAsJsonAsync($"/api/invoices/{draftId}",
            new UpdateInvoiceRequest(invoiceNumber, DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var submitResp = await supplier.PostAsJsonAsync($"/api/invoices/{draftId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        (await Read<InvoiceDetailDto>(submitResp)).Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched));

        return (draftId, invoiceNumber, setup);
    }

    /// <summary>Pushes one invoice-status record (resolved by number) with a unique Idempotency-Key.</summary>
    private async Task<UpsertResultDto> PushInvoiceStatusAsync(string invoiceNumber, string status)
    {
        var inbound = _fx.CreateInboundClient();
        var body = new PushInvoiceStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new InvoiceStatusRecord(status, InvoiceNumber: invoiceNumber),
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/invoice-status")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", $"inv-status-{invoiceNumber}-{status}-{Guid.NewGuid():N}");
        var resp = await inbound.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<UpsertResultDto>(resp)).Data!;
    }

    private async Task<decimal> InvoicedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.InvoicedQtyToDate).FirstAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
