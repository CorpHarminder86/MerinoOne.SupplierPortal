using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Inbound integration guard regressions exercised through the REAL host (X-APIKey scheme) against the dedicated
/// SQL test DB:
/// <list type="bullet">
///   <item><b>(a) Idempotency</b> — replaying an identical inbound batch with the same Idempotency-Key is a 200
///         no-op: every row is reported Skipped and NO second Success SyncLog row is written.</item>
///   <item><b>(b) Scope-gate</b> — the seeded fixture key carries Grn/GrnReceipt/Po/Payment/InvoiceStatus/ErpAck
///         scopes but NOT Tax; a push to <c>/inbound/taxes</c> must be rejected at the policy with 403.</item>
///   <item><b>(c) Anti-spoof</b> — a batch whose body CompanyCode resolves to a company the key is NOT bound to is
///         rejected with 403 and NO rows land (company authority is the KEY, never the body).</item>
/// </list>
/// Every test makes its OWN uniquely-tagged data; money path runs with the scope gate OFF.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InboundGuardsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InboundGuardsTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- (a) idempotency: a replayed batch is a no-op --------------------

    [SkippableFact]
    public async Task Replayed_inbound_batch_with_same_key_is_a_no_op()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Build a fresh chain: PO inbound + supplier ASN (so a GRN can link), then create a covering GRN inbound.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var asnCreate = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        asnCreate.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asnCreate));
        var asn = (await Read<MerinoOne.SupplierPortal.Contracts.Shipments.AsnDetailDto>(asnCreate)).Data!;
        // R5 — submit via Send-for-Approval → buyer Approve.
        var asnSubmit = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asn.Id);
        asnSubmit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asnSubmit));

        var inbound = _fx.CreateInboundClient();
        var grnNumber = $"GRN-IDEM-{setup.Tag}";
        var grnCreate = new PushGoodsReceiptsRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GoodsReceiptRecord(grnNumber, setup.PoNumber, setup.PoPositionNo,
                ReceivedQty: setup.OrderQty, GrnDate: DateTime.UtcNow.Date, AsnNumber: asn.AsnNumber),
        });
        var grnCreateResp = await inbound.PostAsJsonAsync("/api/integration/inbound/goods-receipts", grnCreate);
        grnCreateResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(grnCreateResp));

        // The idempotency target: a grn-status batch with an EXPLICIT key. Replaying it must short-circuit.
        var idemKey = $"idem-{setup.Tag}-{Guid.NewGuid():N}";
        var statusBody = new PushGrnStatusRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new GrnStatusRecord(grnNumber, nameof(GrnStatus.GrnApproved), AsnNumber: asn.AsnNumber),
        });

        var first = await SendWithKeyAsync(inbound, "/api/integration/inbound/grn-status", statusBody, idemKey);
        first.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(first));

        int SuccessSyncLogCount()
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

        var afterFirst = SuccessSyncLogCount();
        afterFirst.Should().BeGreaterThanOrEqualTo(1, because: "the first delivery writes one Success SyncLog");

        // Replay the IDENTICAL batch with the SAME key → no-op.
        var second = await SendWithKeyAsync(inbound, "/api/integration/inbound/grn-status", statusBody, idemKey);
        second.StatusCode.Should().Be(HttpStatusCode.OK, because: "a replay is a 200 no-op, not an error");
        var replay = await Read<UpsertGrnStatusResultDto>(second);
        replay.Data!.Updated.Should().Be(0, because: "a replay touches nothing");
        replay.Data!.Skipped.Should().Be(replay.Data!.Received, because: "every row of a replay is reported Skipped");

        SuccessSyncLogCount().Should().Be(afterFirst, because: "a replayed batch must not write a second Success SyncLog");
    }

    // -------------------- (b) scope-gate: /taxes lacks the Tax scope → 403 --------------------

    [SkippableFact]
    public async Task Push_to_taxes_with_a_key_lacking_the_tax_scope_is_403()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var inbound = _fx.CreateInboundClient();   // the seeded key has NO Integration.Inbound.Tax scope.
        var tag = Guid.NewGuid().ToString("N")[..8];

        var body = new PushTaxesRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new TaxRecord($"GST-{tag}", "GST 18%", 18m, true),
        });

        var resp = await inbound.PostAsJsonAsync("/api/integration/inbound/taxes", body);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the key lacks the Integration.Inbound.Tax scope, so the [Authorize] policy denies the request");

        // Belt-and-braces: the spoof-resistant authority means NO Tax row landed for this tag.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var landed = await db.Taxes.IgnoreQueryFilters().AnyAsync(t => t.Code == $"GST-{tag}");
        landed.Should().BeFalse(because: "a 403 at the policy means the handler never ran and no Tax row was written");
    }

    // -------------------- (c) anti-spoof: body company != key-bound company → 403, no write --------------------

    [SkippableFact]
    public async Task Inbound_batch_with_unbound_body_company_is_403_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // A SECOND company in the SAME tenant as the key, with a distinct resolvable code the key is NOT bound to.
        // (The key is bound to company "2000"; this company resolves but is unbound → anti-spoof 403, not a 400.)
        var spoofCompanyCode = $"SPOOF{tag[..4]}";
        await SeedUnboundCompanyAsync(spoofCompanyCode);

        // Build a legitimate PO under the BOUND company so a real GRN number exists to target.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        var inbound = _fx.CreateInboundClient();

        var grnNumber = $"GRN-SPOOF-{tag}";
        // Push a goods-receipt batch whose BODY CompanyCode is the unbound company. The company authority is the
        // KEY: the resolved (unbound) company fails the anti-spoof gate BEFORE any upsert runs.
        var grnBody = new PushGoodsReceiptsRequest(spoofCompanyCode, new[]
        {
            new GoodsReceiptRecord(grnNumber, setup.PoNumber, setup.PoPositionNo,
                ReceivedQty: setup.OrderQty, GrnDate: DateTime.UtcNow.Date),
        });

        var resp = await inbound.PostAsJsonAsync("/api/integration/inbound/goods-receipts", grnBody);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the body company resolves to a company the key is not bound to (anti-spoof), so the push is rejected");

        // No GRN row landed under EITHER the spoofed company or the bound company for this spoofed batch.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var landed = await db.GoodsReceipts.IgnoreQueryFilters().AnyAsync(g => g.GrnNumber == grnNumber);
        landed.Should().BeFalse(because: "an anti-spoof 403 must not write any cross-company row");
    }

    // ====================================================================================================
    // helpers
    // ====================================================================================================

    /// <summary>Seeds an active company under the KEY's tenant with a code the key is NOT bound to.</summary>
    private async Task SeedUnboundCompanyAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        db.TenantEntities.Add(new TenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            Code = code,
            Name = $"Unbound Co {code}",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client, string path, object body, string idemKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", idemKey);
        return await client.SendAsync(req);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
