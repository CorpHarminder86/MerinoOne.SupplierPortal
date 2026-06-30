using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 — TSD R5 Addendum §10.4 (re-timed from R4 §4.3 / §13 / UC-ASN-06; DI-02; DI-03). The atomic cumulative-shipped
/// guard under REAL row contention on REAL SQL Server — now at the <b>Approve → Submit</b> step (the over-ship guard
/// MOVED there from ASN create). EF InMemory cannot reproduce the conditional <c>ExecuteUpdateAsync</c> WHERE +
/// affected-row-count semantics, so these MUST run relationally.
///
/// <para>Each writer creates a Draft + sends it for approval up front (no contention there — Draft/PendingApproval
/// do not consume balance), then the contended step is N independent buyer-Approve calls fired simultaneously: the
/// approve runs the submit path whose single conditional UPDATE reads OrderQty + ShippedQtyToDate LIVE — exactly
/// what makes the race safe. Each approve is an INDEPENDENT HTTP request → fresh DI scope / context / connection.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnConcurrencyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnConcurrencyTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-ASN-06 — ConcurrentApprovals_OnlyOneCommits_NoOverShip ────────────────────────────────────────
    [SkippableFact]
    public async Task ConcurrentAsns_OnlyOneCommits_NoOverShip()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Line balance 100 (tolerance 0). Two ASNs, each shipping 60, both sent for approval, then approved at once.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var asnA = await CreatePendingAsync(supplier, setup, 60m);
        var asnB = await CreatePendingAsync(supplier, setup, 60m);

        // Two distinct buyer clients → two independent HTTP approve requests (own scope/context/connection).
        var buyerA = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var buyerB = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(2);
        Task<HttpStatusCode> ApproveAsync(HttpClient client, Guid asnId) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await client.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(ApproveAsync(buyerA, asnA), ApproveAsync(buyerB, asnB));

        // EXACTLY ONE commits (200); the other's guard hits 0 rows → ValidationException → 400. No over-ship.
        results.Count(s => s == HttpStatusCode.OK).Should().Be(1, because: "exactly one of the two 60-ships submits (UC-ASN-06)");
        results.Count(s => s == HttpStatusCode.BadRequest).Should().Be(1,
            because: "the loser's guard evaluates 100−60=40 < 60 → 0 rows → rejected");

        (await ShippedToDate(setup.PoLineId)).Should().Be(60m,
            because: "the final cumulative equals the single accepted ship — no over-ship, no lost update");
    }

    // ── DI-02 — Cumulative_NeverReadThenWrite (N concurrent approvals) ───────────────────────────────────
    [SkippableFact]
    public async Task Cumulative_NeverReadThenWrite()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Ceiling exactly 100 (tolerance 0). N approvals each ship 30 → at most 3 fit (3×30=90 ≤ 100; a 4th = 120 > 100).
        const int writers = 8;
        const decimal each = 30m;
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var asnIds = new Guid[writers];
        for (var i = 0; i < writers; i++) asnIds[i] = await CreatePendingAsync(supplier, setup, each);

        var buyers = new HttpClient[writers];
        for (var i = 0; i < writers; i++)
            buyers[i] = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(writers);
        Task<HttpStatusCode> ApproveAsync(HttpClient client, Guid asnId) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await client.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(Enumerable.Range(0, writers).Select(i => ApproveAsync(buyers[i], asnIds[i])));
        var accepted = results.Count(s => s == HttpStatusCode.OK);

        accepted.Should().Be(3, because: "only three 30-ships fit under the 100 ceiling (3×30=90 ≤ 100; a 4th = 120 > 100)");
        var cumulative = await ShippedToDate(setup.PoLineId);
        cumulative.Should().Be(accepted * each, because: "final cumulative == Σ accepted ships (no lost update — DI-02)");
        cumulative.Should().BeLessThanOrEqualTo(100m, because: "the cumulative never exceeds the ceiling under parallel load");
    }

    // ── DI-03 — Guard_ReadsOrderQtyLive_RevisionSafe (at submit) ─────────────────────────────────────────
    [SkippableFact]
    public async Task Guard_ReadsOrderQtyLive_RevisionSafe()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Ship 100 (cumulative 100), then a further 20-ship blocked at orderQty 100 — both at the approve step.
        (await ProcureToPayFlow.CreateAndSubmitAsync(_fx, supplier, setup, shippedQty: 100m))
            .StatusCode.Should().Be(HttpStatusCode.OK, because: "the full order ships against orderQty 100");

        var blockedAsn = await CreatePendingAsync(supplier, setup, 20m);
        var blocked = await ApproveAsBuyerAsync(blockedAsn);
        blocked.Should().Be(HttpStatusCode.BadRequest, because: "a further 20-ship over-ships orderQty 100 (guard blocks at submit)");

        // ERP revises orderQty 100 → 200. The SAME pending 20-ship now SUCCEEDS on re-approve — the guard read the
        // revised orderQty live within the UPDATE (200 − 100 = 100 ≥ 20). The ASN is still PendingApproval after the
        // failed approve (the approval did not flip), so we re-approve it.
        await SetOrderQtyAsync(setup.PoLineId, 200m);
        var retried = await ApproveAsBuyerAsync(blockedAsn);
        retried.Should().Be(HttpStatusCode.OK,
            because: "the guard evaluates orderQty LIVE — after the revision to 200 the 20-ship fits (DI-03)");
        (await ShippedToDate(setup.PoLineId)).Should().Be(120m,
            because: "the accepted ships total 120 against the revised order of 200");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Creates a Draft ASN (no consumption) and sends it for approval → returns the PendingApproval asnId.</summary>
    private async Task<Guid> CreatePendingAsync(HttpClient supplier, ProcureToPayFlow.Setup setup, decimal qty)
    {
        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: qty));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
        return asnId;
    }

    private async Task<HttpStatusCode> ApproveAsBuyerAsync(Guid asnId)
    {
        var buyer = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var resp = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        return resp.StatusCode;
    }

    private async Task<decimal> ShippedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.ShippedQtyToDate).FirstAsync();
    }

    private async Task SetOrderQtyAsync(Guid poLineId, decimal qty)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PurchaseOrderLines.IgnoreQueryFilters().FirstAsync(l => l.Id == poLineId);
        line.OrderQty = qty;
        await db.SaveChangesAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
