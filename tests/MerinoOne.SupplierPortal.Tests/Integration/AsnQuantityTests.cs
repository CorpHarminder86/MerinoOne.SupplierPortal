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
/// R5 — TSD R5 Addendum §10.4 (re-timed from R4 §4 / §7). The atomic cumulative-shipped guard now consumes balance
/// at the <b>Approve → Submit</b> path, NOT at ASN create. So these assertions create a Draft (which does NOT touch
/// shippedQtyToDate), then send for approval + buyer-approve (= submit) and assert the consumption / over-ship
/// outcome at the NEW site. Cancellation of a Submitted ASN still reverses the cumulative (§10.4 / UC-ASN-11).
///
/// <para>The PO is confirmed to Accepted first (gate open). The over-ship CEILING rejection is behind the D3 tenant
/// flag, enabled via <see cref="ProcureToPayHarness.EnableOverShipGuardAsync"/> (paired with the restoring disposable).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnQuantityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnQuantityTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-ASN-01 — FullShipment_SetsBalanceZero (consumption at Approve→Submit) ─────────────────────────
    [SkippableFact]
    public async Task FullShipment_SetsBalanceZero()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // Create a Draft shipping 100 → NO consumption yet (§10.4).
        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 100m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "a Draft ASN does NOT consume balance (§10.4)");
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        // Send for approval + buyer approve (= submit) → consumption happens HERE.
        var submit = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submit));
        (await ShippedToDate(setup.PoLineId)).Should().Be(100m, because: "balance is consumed at Approve→Submit");

        var detail = (await Read<AsnDetailDto>(submit)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        var line = detail.Lines.Single();
        line.ShippedQtyToDate.Should().Be(100m);
        line.Balance.Should().Be(0m, because: "balance = MAX(0, orderQty − shippedQtyToDate)");
    }

    // ── UC-ASN-02 — PartialShipments_DrawDownBalance (multi-ASN, consumed at submit) ─────────────────────
    [SkippableFact]
    public async Task PartialShipments_DrawDownBalance()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // ASN-1 ships 60 → cumulative 60 (after approve→submit), balance 40.
        var asn1 = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 60m);
        asn1.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asn1));
        (await ShippedToDate(setup.PoLineId)).Should().Be(60m);
        (await Read<AsnDetailDto>(asn1)).Data!.Lines.Single().Balance.Should().Be(40m);

        // ASN-2 ships 40 → cumulative 100, balance 0. Both ASNs persist.
        var asn2 = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 40m);
        asn2.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asn2));
        (await ShippedToDate(setup.PoLineId)).Should().Be(100m);
        (await Read<AsnDetailDto>(asn2)).Data!.Lines.Single().Balance.Should().Be(0m);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asnCount = await db.AsnLines.IgnoreQueryFilters().CountAsync(l => l.PurchaseOrderLineId == setup.PoLineId && !l.IsDeleted);
        asnCount.Should().Be(2, because: "both partial ASNs persist (UC-ASN-02)");
    }

    // ── UC-ASN-03 — OverShip_WithinTolerance_Accepted (guard at submit) ──────────────────────────────────
    [SkippableFact]
    public async Task OverShip_WithinTolerance_Accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 5m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // Ship 100 then over-ship 5 → cumulative 105 (at ceiling 105), both consumed at submit.
        (await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 100m))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var over = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 5m);
        over.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(over));
        (await ShippedToDate(setup.PoLineId)).Should().Be(105m, because: "ceiling 105 ≥ 105 ✓ (UC-ASN-03)");

        var line = (await Read<AsnDetailDto>(over)).Data!.Lines.Single();
        line.Balance.Should().Be(0m, because: "nominal balance = MAX(0, 100 − 105) = 0");
        line.OverShipAllowance.Should().Be(0m, because: "the 5% allowance is exhausted at the ceiling");
    }

    // ── UC-ASN-04 / UC-AP-03 — OverShip_BeyondTolerance_BlockedAtSubmit ──────────────────────────────────
    [SkippableFact]
    public async Task OverShip_BeyondTolerance_BlockedZeroRows()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 5m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // Fully ship 100 (cumulative 100) via approval.
        (await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 100m))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // A further 6-ship (would total 106 > 105) — Draft create is FINE (no consumption); the guard fires at submit.
        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 6m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: "Draft create does NOT consume / does NOT guard (§10.4)");
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var submit = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submit.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(submit));
        (await Read<AsnDetailDto>(submit)).Errors.Should()
            .Contain(e => e.Contains("Ship qty exceeds order qty plus over-ship tolerance."),
                because: "the conditional UPDATE affects 0 rows at submit → ValidationException (UC-ASN-04)");

        (await ShippedToDate(setup.PoLineId)).Should().Be(100m, because: "the rejected over-ship left the cumulative at 100");
    }

    // ── UC-ASN-11 — CancelAsn_ReversesCumulative (Submitted reversal) ────────────────────────────────────
    [SkippableFact]
    public async Task CancelAsn_ReversesCumulative()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Ship 40 and submit (via approval) → cumulative 40.
        var submit = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 40m);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submit));
        var asnId = (await Read<AsnDetailDto>(submit)).Data!.Id;
        (await ShippedToDate(setup.PoLineId)).Should().Be(40m);

        // Cancel a SUBMITTED ASN → cumulative decremented by 40 → balance restored to 100.
        var cancel = await client.PostAsync($"/api/asns/{asnId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(cancel));
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "cancel reverses a consumed (Submitted) ASN (UC-ASN-11)");
        await AssertAsnStatus(asnId, AsnStatus.Cancelled);
    }

    // ── §10.4 — CancelDraft_NoReversal (a Draft never consumed, so nothing to reverse) ───────────────────
    [SkippableFact]
    public async Task CancelDraft_NoReversal()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 40m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "a Draft never consumed");

        var cancel = await client.PostAsync($"/api/asns/{asnId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(cancel));
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "cancelling a Draft reverses nothing (§10.4)");
        await AssertAsnStatus(asnId, AsnStatus.Cancelled);
    }

    // ── DI-04 — Balance_Derived_Reconcilable_AllowanceSeparate (after submit) ────────────────────────────
    [SkippableFact]
    public async Task Balance_Derived_Reconcilable_AllowanceSeparate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        await SetItemTolerancePctAsync(setup, 10m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();
        var submit = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, client, setup, shippedQty: 100m);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submit));

        var line = (await Read<AsnDetailDto>(submit)).Data!.Lines.Single();
        line.Balance.Should().Be(0m, because: "displayed balance is nominal (orderQty − shippedQtyToDate)");
        line.OverShipAllowance.Should().Be(10m, because: "the allowance is surfaced separately from balance (DI-04)");

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

    private async Task SetItemTolerancePctAsync(ProcureToPayFlow.Setup setup, decimal pct)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.Items.IgnoreQueryFilters()
            .FirstAsync(i => i.TenantEntityId == IntegrationTestFixture.CompanyId && i.Code == setup.ItemCode);
        item.OverShipTolerancePct = pct;
        await db.SaveChangesAsync();
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
