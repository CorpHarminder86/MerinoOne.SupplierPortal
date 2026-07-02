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
/// R6 — the ATOMIC per-PO-line over-invoice reservation at invoice submit (mirrors the ASN over-ship guard: a
/// conditional <c>ExecuteUpdateAsync</c> whose 0-rows outcome is a 409), local 2-way/3-way matching (header lands
/// Matched or MatchExceptions), and the compensating reservation release on admin Revoke. Runs on REAL SQL through
/// the real host — EF InMemory cannot reproduce the conditional-UPDATE row-count semantics.
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO/ASN per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoiceReservationMatchingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoiceReservationMatchingTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (d) two invoices billing the SAME remaining qty, submitted in parallel ⇒ exactly one 409 ───────
    [SkippableFact]
    public async Task Concurrent_double_submit_over_invoice_yields_exactly_one_conflict()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplier, generatedDraftId, setup, _) = await CreateSubmittedAsnWithDraftAsync();

        // Give the generated draft a real number; create a SECOND manual draft billing the SAME full qty.
        var putResp = await supplier.PutAsJsonAsync($"/api/invoices/{generatedDraftId}",
            new UpdateInvoiceRequest($"INV-RACE-A-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var manualId = await CreateManualDraftAsync(supplier, setup, $"INV-RACE-B-{setup.Tag}",
            matchingType: nameof(MatchingType.TwoWay), asnId: null);

        // Two independent HTTP submits (own scope/context/connection) released simultaneously.
        var clientA = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var clientB = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var barrier = new Barrier(2);
        Task<HttpStatusCode> SubmitAsync(HttpClient client, Guid invoiceId) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await client.PostAsJsonAsync($"/api/invoices/{invoiceId}/submit", new SubmitInvoiceRequest());
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(SubmitAsync(clientA, generatedDraftId), SubmitAsync(clientB, manualId));

        results.Count(s => s == HttpStatusCode.OK).Should().Be(1,
            because: "the balance covers exactly one of the two full-qty invoices");
        results.Count(s => s == HttpStatusCode.Conflict).Should().Be(1,
            because: "the loser's conditional reservation affects 0 rows → 409");

        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty,
            because: "the final cumulative equals the single accepted reservation — no double-invoice, no lost update");
    }

    // ── (h) ThreeWay: a line NOT covered by GRNs ⇒ MatchExceptions; full GRN coverage ⇒ Matched ────────
    [SkippableFact]
    public async Task ThreeWay_submit_without_grn_lands_match_exceptions()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplier, _, setup, asn) = await CreateSubmittedAsnWithDraftAsync();

        // A manual ThreeWay invoice against the ASN — NO GRN exists, so the 3-way check fails per line.
        var invId = await CreateManualDraftAsync(supplier, setup, $"INV-3W-FAIL-{setup.Tag}",
            matchingType: nameof(MatchingType.ThreeWay), asnId: asn.Id);
        var submitResp = await supplier.PostAsJsonAsync($"/api/invoices/{invId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var dto = (await Read<InvoiceDetailDto>(submitResp)).Data!;

        dto.InvoiceStatus.Should().Be(nameof(InvoiceStatus.MatchExceptions),
            because: "3-way requires billed ≤ Σ received of the covering GRNs — none exist");
        dto.SubmittedAt.Should().NotBeNull(because: "submittedAt/by are stamped regardless of the matching outcome");
        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty,
            because: "the reservation is still taken — MatchExceptions holds it (plan D8)");
    }

    [SkippableFact]
    public async Task ThreeWay_submit_with_full_grn_coverage_lands_matched()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplier, _, setup, asn) = await CreateSubmittedAsnWithDraftAsync();

        // Push the covering GRN inbound (linked to the ASN) BEFORE the 3-way submit.
        var inbound = _fx.CreateInboundClient();
        var grnBody = new PushGoodsReceiptsRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GoodsReceiptRecord($"GRN-3W-{setup.Tag}", setup.PoNumber, setup.PoPositionNo,
                ReceivedQty: setup.OrderQty, GrnDate: DateTime.UtcNow.Date, AsnNumber: asn.AsnNumber),
        });
        var grnResp = await inbound.PostAsJsonAsync("/api/integration/inbound/goods-receipts", grnBody);
        grnResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(grnResp));
        (await Read<UpsertResultDto>(grnResp)).Data!.Failed.Should().Be(0);

        var invId = await CreateManualDraftAsync(supplier, setup, $"INV-3W-PASS-{setup.Tag}",
            matchingType: nameof(MatchingType.ThreeWay), asnId: asn.Id);
        var submitResp = await supplier.PostAsJsonAsync($"/api/invoices/{invId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));

        (await Read<InvoiceDetailDto>(submitResp)).Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched),
            because: "billed 10 ≤ received 10 on every line ⇒ all matched ⇒ header Matched");
    }

    // ── (i) revoke releases the reservation; the balance is re-invoiceable and a re-submit succeeds ────
    [SkippableFact]
    public async Task Revoke_releases_the_reservation_and_resubmit_succeeds()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplier, draftId, setup, _) = await CreateSubmittedAsnWithDraftAsync();
        var putResp = await supplier.PutAsJsonAsync($"/api/invoices/{draftId}",
            new UpdateInvoiceRequest($"INV-REV-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var submitResp = await supplier.PostAsJsonAsync($"/api/invoices/{draftId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = (await Read<InvoiceDetailDto>(submitResp)).Data!;
        submitted.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched));
        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty);

        // Pre-post revoke (Matched is revocable in R6) — releases the reservation atomically. Invoice.Revoke is
        // seeded to SuperAdmin + Finance (PermissionCatalog), so the SuperAdmin principal drives it.
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);
        var revokeResp = await admin.PostAsJsonAsync($"/api/invoices/{draftId}/revoke",
            new RevokeInvoiceRequest("release test", submitted.RowVersion));
        revokeResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(revokeResp));
        (await Read<InvoiceDetailDto>(revokeResp)).Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Draft));

        (await InvoicedToDate(setup.PoLineId)).Should().Be(0m,
            because: "revoke subtracts the invoice's billed quantities from invoicedQtyToDate (plan D8)");

        // The released balance is re-invoiceable: the SAME draft re-submits cleanly.
        var resubmit = await supplier.PostAsJsonAsync($"/api/invoices/{draftId}/submit", new SubmitInvoiceRequest());
        resubmit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resubmit));
        (await Read<InvoiceDetailDto>(resubmit)).Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched));
        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty);
    }

    // -------------------- setup helpers --------------------

    private async Task<(HttpClient Supplier, Guid GeneratedDraftId, ProcureToPayFlow.Setup Setup, AsnDetailDto Asn)>
        CreateSubmittedAsnWithDraftAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplier, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var asn = (await Read<AsnDetailDto>(submitResp)).Data!;
        return (supplier, asn.DraftInvoiceId!.Value, setup, asn);
    }

    /// <summary>A manual Draft invoice billing the FULL order qty of the setup's single PO line.</summary>
    private static async Task<Guid> CreateManualDraftAsync(
        HttpClient supplier, ProcureToPayFlow.Setup setup, string invoiceNumber, string matchingType, Guid? asnId)
    {
        var lineAmount = decimal.Round(setup.OrderQty * setup.PriceUnit, 2);
        var body = new CreateInvoiceRequest(
            setup.PoId, asnId, invoiceNumber, DateTime.UtcNow.Date,
            InvoiceAmount: lineAmount, TaxAmount: 0m, NetAmount: lineAmount,
            CurrencyCode: "INR", MatchingType: matchingType,
            EInvoiceIrn: null, EInvoiceAckNo: null, EWayBillNumber: null, Notes: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(setup.PoLineId, setup.ItemCode, null, setup.OrderQty, setup.PriceUnit, lineAmount, null, 0m),
            });
        var resp = await supplier.PostAsJsonAsync("/api/invoices", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        return (await Read<InvoiceDetailDto>(resp)).Data!.Id;
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
