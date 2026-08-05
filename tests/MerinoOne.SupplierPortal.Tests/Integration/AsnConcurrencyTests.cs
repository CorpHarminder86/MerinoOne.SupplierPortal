using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 §10.4, re-timed again by R14. The atomic cumulative-shipped guard under REAL row contention on REAL SQL
/// Server. EF InMemory cannot reproduce the conditional <c>ExecuteUpdateAsync</c> WHERE + affected-row-count
/// semantics, so these MUST run relationally.
///
/// <para><b>R14 — the contended step moved from the buyer's Approve to the supplier's POST.</b> Approval no longer
/// runs the submit path; posting does. So each writer is seeded to <b>Approved</b> up front (no contention there —
/// Draft/PendingApproval/Approved consume nothing), and the race is N independent supplier-Post calls fired
/// simultaneously. The post runs the submit path whose single conditional UPDATE reads OrderQty +
/// ShippedQtyToDate LIVE — exactly what makes the race safe. Each post is an INDEPENDENT HTTP request → fresh DI
/// scope / context / connection.</para>
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

        // Line balance 100 (tolerance 0). Two ASNs, each shipping 60, both confirmed, then POSTED at once.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var asnA = await CreateApprovedAsync(supplier, setup, 60m);
        var asnB = await CreateApprovedAsync(supplier, setup, 60m);

        // Two distinct supplier clients → two independent HTTP post requests (own scope/context/connection).
        var supplierA = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var supplierB = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(2);
        Task<HttpStatusCode> PostAsync(HttpClient client, Guid asnId) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await ProcureToPayFlow.PostAsync(client, asnId);
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(PostAsync(supplierA, asnA), PostAsync(supplierB, asnB));

        // EXACTLY ONE commits (200); the other's guard hits 0 rows → ValidationException → 400. No over-ship.
        results.Count(s => s == HttpStatusCode.OK).Should().Be(1, because: "exactly one of the two 60-ships posts (UC-ASN-06)");
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

        // Ceiling exactly 100 (tolerance 0). N posts each ship 30 → at most 3 fit (3×30=90 ≤ 100; a 4th = 120 > 100).
        const int writers = 8;
        const decimal each = 30m;
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var asnIds = new Guid[writers];
        for (var i = 0; i < writers; i++) asnIds[i] = await CreateApprovedAsync(supplier, setup, each);

        var suppliers = new HttpClient[writers];
        for (var i = 0; i < writers; i++)
            suppliers[i] = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(writers);
        Task<HttpStatusCode> PostAsync(HttpClient client, Guid asnId) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await ProcureToPayFlow.PostAsync(client, asnId);
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(Enumerable.Range(0, writers).Select(i => PostAsync(suppliers[i], asnIds[i])));
        var accepted = results.Count(s => s == HttpStatusCode.OK);

        accepted.Should().Be(3, because: "only three 30-ship posts fit under the 100 ceiling (3×30=90 ≤ 100; a 4th = 120 > 100)");
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

        // Ship 100 (cumulative 100), then a further 20-ship blocked at orderQty 100 — both at the POST step.
        (await ProcureToPayFlow.CreateAndSubmitAsync(_fx, supplier, setup, shippedQty: 100m))
            .StatusCode.Should().Be(HttpStatusCode.OK, because: "the full order ships against orderQty 100");

        var blockedAsn = await CreateApprovedAsync(supplier, setup, 20m);
        var blocked = await PostAsSupplierAsync(blockedAsn);
        blocked.Should().Be(HttpStatusCode.BadRequest, because: "a further 20-ship over-ships orderQty 100 (guard blocks at post)");

        // ERP revises orderQty 100 → 200. The SAME confirmed 20-ship now SUCCEEDS on re-post — the guard read the
        // revised orderQty live within the UPDATE (200 − 100 = 100 ≥ 20). The ASN is still Approved after the
        // failed post (nothing was committed), so we simply post it again.
        await SetOrderQtyAsync(setup.PoLineId, 200m);
        var retried = await PostAsSupplierAsync(blockedAsn);
        retried.Should().Be(HttpStatusCode.OK,
            because: "the guard evaluates orderQty LIVE — after the revision to 200 the 20-ship fits (DI-03)");
        (await ShippedToDate(setup.PoLineId)).Should().Be(120m,
            because: "the accepted ships total 120 against the revised order of 200");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// R14 — creates a Draft ASN (no consumption) and moves it straight to <b>Approved</b> → returns the asnId.
    /// This suite isolates the POST-time atomic over-ship guard under contention, which requires MULTIPLE ASNs
    /// confirmed on the SAME PO at once. The Send-For-Approval UX gate permits only one in-flight ASN per PO
    /// (AsnDraftGate, widened in R14 to cover Approved), so we seed the confirmed state directly here rather than
    /// driving the gated send. The send gate itself is covered by <see cref="AsnDraftGateTests"/>.
    /// The shipment references are stamped too, since POST hard-blocks without them.
    /// </summary>
    private async Task<Guid> CreateApprovedAsync(HttpClient supplier, ProcureToPayFlow.Setup setup, decimal qty)
    {
        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: qty));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
        var now = DateTime.UtcNow;
        db.AsnApprovals.Add(new AsnApproval
        {
            Id = Guid.NewGuid(), AsnId = asn.Id, Status = AsnApprovalStatus.Approved,
            SubmittedBy = "seed", SubmittedOn = now, DecisionBy = "seed", DecisionOn = now,
            SeccodeId = asn.SeccodeId,
            TenantId = asn.TenantId, TenantEntityId = asn.TenantEntityId, CreatedBy = "seed", CreatedOn = now,
        });
        asn.AsnStatus = AsnStatus.Approved;
        asn.InvoiceNo ??= "INV-TEST-1";
        asn.BillOfLading ??= "BOL-TEST-1";
        asn.PackingList ??= "PL-TEST-1";
        asn.UpdatedBy = "seed";
        asn.UpdatedOn = now;
        await db.SaveChangesAsync();
        return asnId;
    }

    private async Task<HttpStatusCode> PostAsSupplierAsync(Guid asnId)
    {
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await ProcureToPayFlow.PostAsync(supplier, asnId);
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
