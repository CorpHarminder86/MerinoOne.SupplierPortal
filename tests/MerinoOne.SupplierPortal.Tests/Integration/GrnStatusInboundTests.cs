using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Money-path integration regressions exercised through the REAL host (HTTP via the factory client + the
/// X-APIKey scheme) against a dedicated SQL test DB. These live at the EF/SQL boundary, which is exactly
/// where the bugs this session reproduced — a pure unit test (no DB) cannot see them:
///   (a) the multi-row <c>ToDictionary</c> 500 on a GRN with two PO-position rows under one GrnNumber;
///   (b) the AuditEntry Operation CHECK-constraint 500 on the invoice auto-post cascade;
///   (c) inbound idempotency (a replayed batch is a no-op, no duplicate rows).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class GrnStatusInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public GrnStatusInboundTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- (a) multi-row GrnNumber must not 500 --------------------

    [SkippableFact]
    public async Task GrnStatus_two_po_positions_under_one_grn_returns_200()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = _fx.CreateInboundClient();

        // GrnNumber maps to TWO GoodsReceipt rows (one per PO position). Before the fix this threw
        // "An item with the same key has already been added" on the ToDictionary-by-GrnNumber → 500.
        var body = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(IntegrationTestFixture.GrnNumber, nameof(GrnStatus.GrnApproved),
                AsnNumber: IntegrationTestFixture.AsnNumber),
        });

        // Unique Idempotency-Key so this batch ALWAYS runs the multi-row grouping upsert (never collapses into a
        // payload-hash replay short-circuit when another test approved the same GRN first — test-order independence).
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/grn-status")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", $"int-test-grn-multirow-{Guid.NewGuid():N}");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a multi-line GRN must group by GrnNumber, not ToDictionary (regression)");

        var result = await Read<UpsertGrnStatusResultDto>(resp);
        result.Success.Should().BeTrue();
        result.Data!.Failed.Should().Be(0);
        // Both PO-position rows moved together under the one GrnNumber → the upsert ran (not a replay no-op).
        result.Data!.Updated.Should().BeGreaterThanOrEqualTo(1,
            because: "the two PO-position rows under the one GrnNumber are updated together");
    }

    // -------------------- (b) approve → auto-post cascade must not 500, enqueues InvoicePost --------------------

    [SkippableFact]
    public async Task GrnStatus_approve_completes_invoice_coverage_enqueues_post_no_500()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = _fx.CreateInboundClient();

        // Approving the GRN (both covering rows) for a Submitted invoice fires the auto-post cascade, which
        // writes a system-actor AuditEntry. Before the fix that AuditEntry used Operation="AutoPost", which
        // violates CK_AuditEntry_operation IN ('Insert','Update','Delete') → the whole cascade SaveChanges 500'd.
        var body = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(IntegrationTestFixture.GrnNumber, nameof(GrnStatus.GrnApproved),
                AsnNumber: IntegrationTestFixture.AsnNumber),
        });

        var resp = await client.PostAsJsonAsync("/api/integration/inbound/grn-status", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the auto-post AuditEntry must use Operation='Update' (regression for the CHECK 500)");

        var result = await Read<UpsertGrnStatusResultDto>(resp);
        result.Success.Should().BeTrue();
        result.Data!.Failed.Should().Be(0);

        // Assert the cascade's persistent effect at the SQL boundary: an InvoicePost outbox row was enqueued
        // for the invoice (deterministic, written synchronously inside the request's transaction).
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var outboxEnqueued = await db.OutboxMessages.IgnoreQueryFilters()
            .AnyAsync(m => !m.IsDeleted
                           && m.TenantId == IntegrationTestFixture.TenantId
                           && m.EntityId == IntegrationTestFixture.InvoiceId
                           && m.TransactionType == OutboxTransactionType.InvoicePost);
        outboxEnqueued.Should().BeTrue(because: "completing GRN coverage on a Submitted invoice enqueues its ERP post");

        // The invoice claim stamped erpPostInitiatedAt (the S1 atomic claim) — proof the cascade ran end-to-end.
        var initiated = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.Id == IntegrationTestFixture.InvoiceId)
            .Select(i => i.ErpPostInitiatedAt)
            .FirstOrDefaultAsync();
        initiated.Should().NotBeNull(because: "the atomic post-claim stamps erpPostInitiatedAt");

        // The system-actor audit row was written with a CHECK-valid Operation (the bug under test). If the old
        // 'AutoPost' value had been used, the cascade SaveChanges would have 500'd and we'd never reach here.
        var auditOps = await db.AuditEntries
            .Where(a => a.EntityName == nameof(MerinoOne.SupplierPortal.Domain.Entities.Proc.Invoice)
                        && a.EntityId == IntegrationTestFixture.InvoiceId
                        && a.ChangedBy == "system:grn-autopost")
            .Select(a => a.Operation)
            .ToListAsync();
        auditOps.Should().OnlyContain(op => op == "Insert" || op == "Update" || op == "Delete");

        // Dispatcher effect (soft): the kept-running OutboxDispatcherWorker should drain the InvoicePost via the
        // Mock service and write an OUTBOUND Invoice InforSyncLog within a few poll cycles. Poll up to ~12s.
        var outboundLogged = await PollAsync(TimeSpan.FromSeconds(12), async () =>
        {
            using var s = _fx.Factory.Services.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            return await d.InforSyncLogs.IgnoreQueryFilters().AnyAsync(l =>
                l.TenantId == IntegrationTestFixture.TenantId &&
                l.EntityName == OutboxEntity.Invoice &&
                l.Direction == SyncDirection.Outbound);
        });
        outboundLogged.Should().BeTrue(
            because: "the OutboxDispatcherWorker (Mock mode) drains the enqueued InvoicePost and logs it outbound");
    }

    // -------------------- (c) idempotency: a replayed batch is a no-op --------------------

    [SkippableFact]
    public async Task GrnStatus_replayed_batch_is_skipped_no_duplicate_effects()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = _fx.CreateInboundClient();
        const string idemKey = "int-test-grn-idem-001";

        var body = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(IntegrationTestFixture.GrnNumber, nameof(GrnStatus.GrnApproved),
                AsnNumber: IntegrationTestFixture.AsnNumber),
        });

        // First delivery with an explicit Idempotency-Key.
        var first = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/grn-status")
        {
            Content = JsonContent.Create(body),
        };
        first.Headers.Add("Idempotency-Key", idemKey);
        var firstResp = await client.SendAsync(first);
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);

        int SyncLogCount() // count Success inbound Grn logs for this idempotency key
        {
            using var scope = _fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return db.InforSyncLogs.IgnoreQueryFilters().Count(l =>
                l.TenantId == IntegrationTestFixture.TenantId &&
                l.EntityName == nameof(TransactionalInboundEntity.Grn) &&
                l.Direction == SyncDirection.Inbound &&
                l.Status == SyncStatus.Success &&
                l.IdempotencyKey == idemKey);
        }

        var afterFirst = SyncLogCount();
        afterFirst.Should().BeGreaterThanOrEqualTo(1, because: "the first delivery writes one Success SyncLog");

        // Second, identical delivery with the SAME key → the executor short-circuits (prior-Success replay).
        var second = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/grn-status")
        {
            Content = JsonContent.Create(body),
        };
        second.Headers.Add("Idempotency-Key", idemKey);
        var secondResp = await client.SendAsync(second);
        secondResp.StatusCode.Should().Be(HttpStatusCode.OK, because: "a replay is a 200 no-op, not an error");

        var replay = await Read<UpsertGrnStatusResultDto>(secondResp);
        replay.Data!.Updated.Should().Be(0, because: "a replay touches nothing");
        replay.Data!.Skipped.Should().Be(replay.Data!.Received, because: "every row of a replay is reported Skipped");

        // No NEW Success inbound SyncLog row for this key (the replay did not re-run the upsert).
        SyncLogCount().Should().Be(afterFirst, because: "a replayed batch must not write a second Success SyncLog");
    }

    // -------------------- (d) R6 grouped invoices: the GRN links to the LINE-correlated invoice --------------------

    [SkippableFact]
    public async Task GrnStatus_on_grouped_asn_links_grn_to_the_invoice_billing_its_po_line()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // ONE supplier, TWO Accepted POs in different currencies → the ASN generates TWO grouped draft invoices.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poInr = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, currencyCode: "INR", confirm: true);
        var poUsd = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, currencyCode: "USD", confirm: true);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, poInr.PoId);

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = new CreateAsnRequest(
            PurchaseOrderId: null, PurchaseOrderIds: new[] { poInr.PoId, poUsd.PoId },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: "TRK",
            VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(poInr.PoLineId, ShippedQty: poInr.OrderQty, BatchNumber: null, ExpiryDate: null),
                new(poUsd.PoLineId, ShippedQty: poUsd.OrderQty, BatchNumber: null, ExpiryDate: null),
            });
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asn = (await Read<AsnDetailDto>(createResp)).Data!;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asn.Id);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceIds.Should().HaveCount(2);

        Guid invoiceInrId, invoiceUsdId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoices = await db.Invoices.IgnoreQueryFilters()
                .Where(i => i.AsnId == asn.Id && !i.IsDeleted)
                .Select(i => new { i.Id, i.CurrencyCode })
                .ToListAsync();
            invoiceInrId = invoices.Single(i => i.CurrencyCode == "INR").Id;
            invoiceUsdId = invoices.Single(i => i.CurrencyCode == "USD").Id;
        }

        // A GRN for the USD (group-B) PO line, linked to the ASN, then a status push (which stamps InvoiceId).
        var inbound = _fx.CreateInboundClient();
        var grnNumber = $"GRN-LINK-{tag}";
        var grnCreate = new PushGoodsReceiptsRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GoodsReceiptRecord(grnNumber, poUsd.PoNumber, poUsd.PoPositionNo,
                ReceivedQty: poUsd.OrderQty, GrnDate: DateTime.UtcNow.Date, AsnNumber: asn.AsnNumber),
        });
        var grnCreateResp = await inbound.PostAsJsonAsync("/api/integration/inbound/goods-receipts", grnCreate);
        grnCreateResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(grnCreateResp));
        (await Read<UpsertResultDto>(grnCreateResp)).Data!.Failed.Should().Be(0);

        var statusBody = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(grnNumber, nameof(GrnStatus.GrnApproved), AsnNumber: asn.AsnNumber),
        });
        var statusReq = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/grn-status")
        {
            Content = JsonContent.Create(statusBody),
        };
        statusReq.Headers.Add("Idempotency-Key", $"grn-link-{tag}-{Guid.NewGuid():N}");
        var statusResp = await inbound.SendAsync(statusReq);
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(statusResp));
        (await Read<UpsertGrnStatusResultDto>(statusResp)).Data!.Failed.Should().Be(0);

        // The link must be LINE-correlated: the GRN bills the USD PO line ⇒ invoice B (USD), never the sibling.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linked = await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(g => g.GrnNumber == grnNumber && !g.IsDeleted)
                .Select(g => g.InvoiceId)
                .FirstAsync();
            linked.Should().Be(invoiceUsdId,
                because: "with N grouped invoices per ASN, the GRN links to the invoice whose lines bill its PO line");
            linked.Should().NotBe(invoiceInrId);
        }
    }

    // -------------------- helpers --------------------

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<bool> PollAsync(TimeSpan timeout, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return await condition();
    }
}
