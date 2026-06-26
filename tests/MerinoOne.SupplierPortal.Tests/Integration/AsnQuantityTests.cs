using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §4 / §7 / UC-ASN-01..04,11; DI-04. The ASN quantity-control core on REAL SQL, through the
/// real host: the atomic cumulative-shipped guard draws the balance down, accepts an over-ship WITHIN tolerance,
/// rejects an over-ship BEYOND tolerance via the conditional UPDATE's 0-row outcome, and the ASN-cancel reverses
/// the cumulative. DI-04 checks the derived balance + the separately-surfaced over-ship allowance, and that
/// <c>shippedQtyToDate</c> reconciles to Σ AsnLine.shippedQty over the non-cancelled ASNs.
///
/// <para>The PO is confirmed to Accepted first (gate open) so these assertions isolate the QUANTITY guard, not the
/// confirmation gate. The over-ship CEILING rejection is behind the D3 tenant flag, so UC-ASN-03/04 enable it via
/// <see cref="ProcureToPayHarness.EnableOverShipGuardAsync"/> (always paired with the restoring disposable).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnQuantityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnQuantityTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-ASN-01 — FullShipment_SetsBalanceZero ────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task FullShipment_SetsBalanceZero()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // orderQty 100, tolerance 0, shippedQtyToDate 0 → ship 100.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        var resp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 100m));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));

        // cumulative == 100; nominal balance 0.
        (await ShippedToDate(setup.PoLineId)).Should().Be(100m);
        var detail = (await Read<AsnDetailDto>(resp)).Data!;
        var line = detail.Lines.Single();
        line.ShippedQtyToDate.Should().Be(100m);
        line.Balance.Should().Be(0m, because: "balance = MAX(0, orderQty − shippedQtyToDate)");
        await SubmitOk(client, detail.Id);
    }

    // ── UC-ASN-02 — PartialShipments_DrawDownBalance (multi-ASN) ─────────────────────────────────────────
    [SkippableFact]
    public async Task PartialShipments_DrawDownBalance()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // ASN-1 ships 60 → cumulative 60, balance 40.
        var asn1 = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 60m));
        asn1.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asn1));
        (await ShippedToDate(setup.PoLineId)).Should().Be(60m);
        (await Read<AsnDetailDto>(asn1)).Data!.Lines.Single().Balance.Should().Be(40m);

        // ASN-2 ships 40 → cumulative 100, balance 0. Both ASNs persist.
        var asn2 = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 40m));
        asn2.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asn2));
        (await ShippedToDate(setup.PoLineId)).Should().Be(100m);
        (await Read<AsnDetailDto>(asn2)).Data!.Lines.Single().Balance.Should().Be(0m);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asnCount = await db.AsnLines.IgnoreQueryFilters().CountAsync(l => l.PurchaseOrderLineId == setup.PoLineId && !l.IsDeleted);
        asnCount.Should().Be(2, because: "both partial ASNs persist (UC-ASN-02)");
    }

    // ── UC-ASN-03 — OverShip_WithinTolerance_Accepted ───────────────────────────────────────────────────
    [SkippableFact]
    public async Task OverShip_WithinTolerance_Accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // orderQty 100, item tolerance 5% (ceiling 105). Ship 100 then over-ship 5 → cumulative 105 (at ceiling).
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 5m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        (await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 100m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var over = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 5m));
        over.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(over));
        (await ShippedToDate(setup.PoLineId)).Should().Be(105m, because: "ceiling 105 ≥ 105 ✓ (UC-ASN-03)");

        // Nominal balance reads 0 while the allowance is now exhausted (DI-04 separation).
        var line = (await Read<AsnDetailDto>(over)).Data!.Lines.Single();
        line.Balance.Should().Be(0m, because: "nominal balance = MAX(0, 100 − 105) = 0");
        line.OverShipAllowance.Should().Be(0m, because: "the 5% allowance is exhausted at the ceiling");
    }

    // ── UC-ASN-04 — OverShip_BeyondTolerance_BlockedZeroRows ────────────────────────────────────────────
    [SkippableFact]
    public async Task OverShip_BeyondTolerance_BlockedZeroRows()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // orderQty 100, tolerance 5% (ceiling 105), already 100 shipped. Ship 6 (would total 106 > 105) → 0 rows.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 5m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        (await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 100m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var beyond = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 6m));
        beyond.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(beyond));
        (await Read<AsnDetailDto>(beyond)).Errors.Should()
            .Contain(e => e.Contains("Ship qty exceeds order qty plus over-ship tolerance."),
                because: "the conditional UPDATE affects 0 rows → ValidationException (UC-ASN-04)");

        // cumulative UNCHANGED (still 100) — the guard rolled back, no partial drift.
        (await ShippedToDate(setup.PoLineId)).Should().Be(100m, because: "the rejected over-ship left the cumulative at 100");
    }

    // ── UC-ASN-11 — CancelAsn_ReversesCumulative ────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task CancelAsn_ReversesCumulative()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Ship 40 → cumulative 40, then Submit so the ASN is a "real" shipment.
        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 40m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        await SubmitOk(client, asnId);
        (await ShippedToDate(setup.PoLineId)).Should().Be(40m);

        // Cancel → cumulative decremented by 40 → balance restored to 100.
        var cancel = await client.PostAsync($"/api/asns/{asnId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(cancel));
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "the cancel reverses the cumulative (UC-ASN-11)");
        await AssertAsnStatus(asnId, AsnStatus.Cancelled);
    }

    // ── DI-04 — Balance_Derived_Reconcilable_AllowanceSeparate ──────────────────────────────────────────
    [SkippableFact]
    public async Task Balance_Derived_Reconcilable_AllowanceSeparate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // orderQty 100, tolerance 10% (ceiling 110). Ship 100 → balance reads 0 but a 10-unit allowance REMAINS.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 10m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        var resp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 100m));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));

        var line = (await Read<AsnDetailDto>(resp)).Data!.Lines.Single();
        // Balance is the NOMINAL derived figure (never persisted) = MAX(0, 100−100) = 0 …
        line.Balance.Should().Be(0m, because: "displayed balance is nominal (orderQty − shippedQtyToDate)");
        // … while the over-ship allowance is surfaced SEPARATELY: MAX(0, 100×1.10 − 100) = 10.
        line.OverShipAllowance.Should().Be(10m, because: "the allowance is surfaced separately from balance (DI-04)");

        // shippedQtyToDate reconciles to Σ AsnLine.shippedQty over the non-cancelled ASNs.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sumNonCancelled = await db.AsnLines.IgnoreQueryFilters()
            .Where(l => l.PurchaseOrderLineId == setup.PoLineId && !l.IsDeleted
                        && l.Asn!.AsnStatus != AsnStatus.Cancelled)
            .SumAsync(l => (decimal?)l.ShippedQty) ?? 0m;
        var cumulative = await db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => l.Id == setup.PoLineId).Select(l => l.ShippedQtyToDate).FirstAsync();
        cumulative.Should().Be(sumNonCancelled,
            because: "shippedQtyToDate reconciles to Σ AsnLine.shippedQty over non-cancelled ASNs (DI-04)");
        cumulative.Should().Be(100m);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private async Task<decimal> ShippedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.ShippedQtyToDate).FirstAsync();
    }

    // Sets the OverShipTolerancePct on the inv.Item that the PO line resolves by (company, ItemCode).
    private async Task SetItemTolerancePctAsync(ProcureToPayFlow.Setup setup, decimal pct)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.Items.IgnoreQueryFilters()
            .FirstAsync(i => i.TenantEntityId == IntegrationTestFixture.CompanyId && i.Code == setup.ItemCode);
        item.OverShipTolerancePct = pct;
        await db.SaveChangesAsync();
    }

    private static async Task SubmitOk(HttpClient client, Guid asnId)
    {
        var resp = await client.PostAsJsonAsync($"/api/asns/{asnId}/submit", new SubmitAsnRequest());
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
    }

    private async Task AssertAsnStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId)
            .Select(a => a.AsnStatus).FirstAsync();
        status.Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
