using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §4.3 / §13 / UC-ASN-06; DI-02; DI-03. The atomic cumulative-shipped guard under REAL row
/// contention on REAL SQL Server. EF InMemory cannot reproduce the conditional <c>ExecuteUpdateAsync</c> WHERE +
/// affected-row-count semantics the guard relies on, so these MUST run relationally.
///
/// <para>Each concurrent writer is an INDEPENDENT HTTP <c>POST /api/asns</c> as the supplier. The real host resolves
/// a fresh per-request DI scope → a fresh scoped <see cref="AppDbContext"/> on its OWN pooled SQL connection, so the
/// writers genuinely contend at the database (separate contexts / connections), not over one shared context. The D3
/// over-ship CEILING flag is enabled for the duration (its rejection is what enforces the ceiling under contention);
/// the cumulative increment itself is the single conditional UPDATE that reads OrderQty + ShippedQtyToDate LIVE
/// inside the statement (never a C# read-then-write), which is exactly what makes the race safe.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnConcurrencyTests
{
    private readonly IntegrationTestFixture _fx;
    public AsnConcurrencyTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-ASN-06 — ConcurrentAsns_OnlyOneCommits_NoOverShip ─────────────────────────────────────────────
    [SkippableFact]
    public async Task ConcurrentAsns_OnlyOneCommits_NoOverShip()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Line balance 100 (tolerance 0). Two ASNs, each shipping 60, fired simultaneously on SEPARATE connections.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // Two distinct authenticated supplier clients → two independent HTTP requests (own scope/context/connection).
        var clientA = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var clientB = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(2);
        // Task.Run so BOTH writers are scheduled on their own pool threads BEFORE either reaches the (blocking)
        // barrier — otherwise the first SignalAndWait would block the caller before the second task is even created.
        Task<HttpStatusCode> ShipSixtyAsync(HttpClient client) => Task.Run(async () =>
        {
            barrier.SignalAndWait();   // release both writers at the same instant.
            var resp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 60m));
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(ShipSixtyAsync(clientA), ShipSixtyAsync(clientB));

        // EXACTLY ONE commits (200); the other's guard hits 0 rows → ValidationException → 400. No over-ship, no lost update.
        results.Count(s => s == HttpStatusCode.OK).Should().Be(1, because: "exactly one of the two 60-ships commits (UC-ASN-06)");
        results.Count(s => s == HttpStatusCode.BadRequest).Should().Be(1,
            because: "the loser's guard evaluates 100−60=40 < 60 → 0 rows → rejected");

        (await ShippedToDate(setup.PoLineId)).Should().Be(60m,
            because: "the final cumulative equals the single accepted ship — no over-ship, no lost update");
    }

    // ── DI-02 — Cumulative_NeverReadThenWrite (N concurrent writers) ─────────────────────────────────────
    [SkippableFact]
    public async Task Cumulative_NeverReadThenWrite()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Ceiling exactly 100 (tolerance 0). N writers each ship 30 → at most 3 fit (3×30=90 ≤ 100; a 4th = 120 > 100).
        // Under the single conditional UPDATE the final cumulative == Σ accepted, never exceeds the ceiling — proving
        // there is no read-then-write path that could lose an update or over-ship.
        const int writers = 8;
        const decimal each = 30m;
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // One independent client per writer → one independent per-request context/connection.
        var clients = new HttpClient[writers];
        for (var i = 0; i < writers; i++)
            clients[i] = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var barrier = new Barrier(writers);
        Task<HttpStatusCode> ShipAsync(HttpClient client) => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var resp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: each));
            return resp.StatusCode;
        });

        var results = await Task.WhenAll(clients.Select(ShipAsync));
        var accepted = results.Count(s => s == HttpStatusCode.OK);

        accepted.Should().Be(3, because: "only three 30-ships fit under the 100 ceiling (3×30=90 ≤ 100; a 4th = 120 > 100)");
        var cumulative = await ShippedToDate(setup.PoLineId);
        cumulative.Should().Be(accepted * each, because: "final cumulative == Σ accepted ships (no lost update — DI-02)");
        cumulative.Should().BeLessThanOrEqualTo(100m, because: "the cumulative never exceeds the ceiling under parallel load");
    }

    // ── DI-03 — Guard_ReadsOrderQtyLive_RevisionSafe ────────────────────────────────────────────────────
    // A qty revision lands between two guard runs; the guard reads OrderQty LIVE inside the conditional UPDATE, so
    // the outcome reflects the value AT EXECUTION TIME, never a stale snapshot. A ship blocked at the OLD orderQty
    // succeeds once an ERP revision raises orderQty — proving the live read (no cached/stale orderQty).
    [SkippableFact]
    public async Task Guard_ReadsOrderQtyLive_RevisionSafe()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // orderQty 100, tolerance 0. Ship 100 (cumulative 100). A further 20-ship is blocked at orderQty 100.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        async Task<HttpStatusCode> ShipAsync(decimal qty)
        {
            var resp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: qty));
            return resp.StatusCode;
        }

        (await ShipAsync(100m)).Should().Be(HttpStatusCode.OK, because: "the full order ships against orderQty 100");
        (await ShipAsync(20m)).Should().Be(HttpStatusCode.BadRequest, because: "a further 20-ship over-ships orderQty 100 (guard blocks)");

        // ERP revises orderQty 100 → 200 (a live qty revision). The SAME 20-ship now SUCCEEDS — the guard read the
        // revised orderQty live within the UPDATE (200 − 100 = 100 ≥ 20), not the stale 100.
        await SetOrderQtyAsync(setup.PoLineId, 200m);
        (await ShipAsync(20m)).Should().Be(HttpStatusCode.OK,
            because: "the guard evaluates orderQty LIVE — after the revision to 200 the 20-ship fits (DI-03)");
        (await ShippedToDate(setup.PoLineId)).Should().Be(120m,
            because: "the accepted ships total 120 against the revised order of 200");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
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
}
