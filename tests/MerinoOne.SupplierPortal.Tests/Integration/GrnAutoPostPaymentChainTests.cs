using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
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
/// The full money path through the REAL host, end-to-end on a fresh tagged supplier (so it never collides with
/// the fixture's shared seed): push a PO inbound → supplier creates+submits an ASN (auto-creating a draft
/// invoice) → supplier edits + submits the invoice (Submitted) → create the covering GRN inbound (NotApproved)
/// linked to the ASN → push grn-status GrnApproved (FRESH GrnNumber so idempotency never dedups) → the invoice
/// auto-posts (an InvoicePost outbox row is enqueued for it + erpPostInitiatedAt stamped) → push a payment
/// inbound → the Payment row persists against the invoice.
///
/// <para>Money path: scope gate OFF; the chain is built per-test with a unique tag.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class GrnAutoPostPaymentChainTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public GrnAutoPostPaymentChainTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Full_chain_submit_invoice_grn_approve_auto_posts_then_payment_persists()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // 1. PO inbound + supplier-side ASN create/submit (auto-creates the draft invoice).
        // R5 — submit is now reached through Send-for-Approval → buyer Approve (the draft invoice is created there).
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asn = (await Read<AsnDetailDto>(createResp)).Data!;
        var asnId = asn.Id;
        var asnNumber = asn.AsnNumber;

        var asnSubmitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        asnSubmitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asnSubmitResp));
        var draftInvoiceId = (await Read<AsnDetailDto>(asnSubmitResp)).Data!.DraftInvoiceId!.Value;

        // 2. Supplier edits the draft invoice (real number) + submits it → Submitted (auto-post precondition).
        var realNumber = $"INV-CHAIN-{setup.Tag}";
        var put = new UpdateInvoiceRequest(realNumber, DateTime.UtcNow.Date, null, null, null, null);
        var putResp = await supplierClient.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var invSubmitResp = await supplierClient.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        invSubmitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(invSubmitResp));
        // R6 — local matching at submit: the TwoWay draft (no GRN existed at generation) passes its reservation
        // and lands Matched. The GRN auto-post claim accepts Submitted OR Matched (plan D9), so the chain holds.
        (await Read<InvoiceDetailDto>(invSubmitResp)).Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched));

        // 3. Create the covering GRN inbound (NotApproved), linked to the ASN so the GRN→Invoice link resolves.
        var inbound = _fx.CreateInboundClient();
        var grnNumber = $"GRN-CHAIN-{setup.Tag}";   // FRESH number — idempotency never dedups this row.
        var grnCreate = new PushGoodsReceiptsRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GoodsReceiptRecord(grnNumber, setup.PoNumber, setup.PoPositionNo,
                ReceivedQty: setup.OrderQty, GrnDate: DateTime.UtcNow.Date, AsnNumber: asnNumber),
        });
        var grnCreateResp = await inbound.PostAsJsonAsync("/api/integration/inbound/goods-receipts", grnCreate);
        grnCreateResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(grnCreateResp));
        (await Read<UpsertResultDto>(grnCreateResp)).Data!.Failed.Should().Be(0,
            because: "the PO + line resolve, so the GRN row inserts");

        // 4. Push grn-status GrnApproved → invoice auto-posts.
        var grnStatus = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(grnNumber, nameof(GrnStatus.GrnApproved), AsnNumber: asnNumber),
        });
        var statusReq = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/grn-status")
        {
            Content = JsonContent.Create(grnStatus),
        };
        statusReq.Headers.Add("Idempotency-Key", $"chain-grn-{setup.Tag}-{Guid.NewGuid():N}");
        var statusResp = await inbound.SendAsync(statusReq);
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(statusResp));
        var statusResult = await Read<UpsertGrnStatusResultDto>(statusResp);
        statusResult.Data!.Failed.Should().Be(0);
        statusResult.Data!.AutoPostsEnqueued.Should().Be(1,
            because: "approving the sole covering GRN of a Submitted/Matched invoice enqueues its ERP post (R6 widened claim)");

        // Assert the auto-post side-effects at the SQL boundary.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var outboxEnqueued = await db.OutboxMessages.IgnoreQueryFilters()
                .AnyAsync(m => !m.IsDeleted
                               && m.EntityId == draftInvoiceId
                               && m.TransactionType == OutboxTransactionType.InvoicePost);
            outboxEnqueued.Should().BeTrue(because: "the GRN-approved Submitted invoice enqueues its InvoicePost");

            var initiated = await db.Invoices.IgnoreQueryFilters()
                .Where(i => i.Id == draftInvoiceId).Select(i => i.ErpPostInitiatedAt).FirstAsync();
            initiated.Should().NotBeNull(because: "the atomic post-claim stamps erpPostInitiatedAt");
        }

        // 5. Push the payment inbound → Payment row persists against the invoice.
        var paymentRef = $"PAY-CHAIN-{setup.Tag}";
        var payBody = new PushPaymentsRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PaymentRecord(paymentRef, NetPaid: setup.OrderQty * setup.PriceUnit,
                InvoiceNumber: realNumber, PaymentAmount: setup.OrderQty * setup.PriceUnit,
                PaymentDate: DateTime.UtcNow.Date, PaymentMode: "NEFT"),
        });
        var payResp = await inbound.PostAsJsonAsync("/api/integration/inbound/payments", payBody);
        payResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(payResp));
        var payResult = await Read<UpsertResultDto>(payResp);
        payResult.Data!.Failed.Should().Be(0, because: "the invoice resolves by number, so the payment writes");

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.PaymentReference == paymentRef && p.InvoiceId == draftInvoiceId);
            payment.Should().NotBeNull(because: "the inbound payment persists against the resolved invoice");
            payment!.NetPaid.Should().Be(setup.OrderQty * setup.PriceUnit);
        }
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
