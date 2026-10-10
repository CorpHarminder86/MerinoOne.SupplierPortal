using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AsnShipQtyScale = MerinoOne.SupplierPortal.Application.Shipments.Commands.AsnShipQtyScale;
using PurchaseOrderLine = MerinoOne.SupplierPortal.Domain.Entities.Proc.PurchaseOrderLine;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

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

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // 2026-10-10 — ship-quantity PRECISION. A line may ship only as many decimal places as the unit master
    // allows for its PO line's order unit (mdm.Unit.decimalPlaces; unknown unit → 3). The rule is enforced by
    // CreateAsn + UpdateAsn (HTTP 400, one message per offending line) and every ASN DTO carries the precision.
    // Units are the suite's own, namespaced UT-ASNQ-* codes (created once, left in place); the PO line under
    // test is re-pointed at one in the DB — it is this test's own tagged PO, no other test reads it.
    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    private const string ZeroDecimalsUnit = "UT-ASNQ-0";
    private const string TwoDecimalsUnit = "UT-ASNQ-2";

    // The 400 body carries messages only, so the message locates the line itself: PO number, line position, unit, precision.
    private static string ScaleMessage(ProcureToPayFlow.Setup setup, string unit, int places)
        => $"Ship quantity on {setup.PoNumber} line {setup.PoPositionNo} for unit {unit} allows at most {places} decimal places.";

    // scale > n → 400 naming the PO line + unit; whole numbers (however spelled) are accepted; nothing is persisted on rejection.
    [SkippableFact]
    public async Task ShipQty_with_more_decimals_than_the_unit_allows_is_rejected_on_create()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await SeedPoWithUnitAsync(ZeroDecimalsUnit, decimals: 0);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var bad = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 2.5m));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(bad));
        (await Read<AsnDetailDto>(bad)).Errors.Should().Equal(
            new[] { ScaleMessage(setup, ZeroDecimalsUnit, 0) },
            because: "one message per offending line, naming its PO and line position, the unit and the precision it allows");
        (await AsnLineCountAsync(setup.PoLineId)).Should().Be(0, because: "a rejected create persists nothing");

        // 3.0m is a whole number with a trailing zero: the rule judges the VALUE, not how many digits were typed.
        var ok = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 3.0m));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));
        var line = (await Read<AsnDetailDto>(ok)).Data!.Lines.Single();
        line.ShippedQty.Should().Be(3m);
        line.UnitDecimalPlaces.Should().Be(0, because: "the ASN DTO carries the unit master's precision for the line");
    }

    // exactly n decimals is accepted (the boundary), n+1 is not.
    [SkippableFact]
    public async Task ShipQty_with_exactly_the_unit_decimals_is_accepted_and_one_more_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await SeedPoWithUnitAsync(TwoDecimalsUnit, decimals: 2);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var tooMany = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 1.255m));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(tooMany));
        (await Read<AsnDetailDto>(tooMany)).Errors.Should().Equal(new[] { ScaleMessage(setup, TwoDecimalsUnit, 2) });

        var exact = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 1.25m));
        exact.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(exact));
        var line = (await Read<AsnDetailDto>(exact)).Data!.Lines.Single();
        line.ShippedQty.Should().Be(1.25m);
        line.UnitDecimalPlaces.Should().Be(2);
    }

    // a unit that is not in the master → the fallback of 3: 3 decimals accepted, 4 rejected.
    [SkippableFact]
    public async Task ShipQty_on_an_unknown_unit_allows_up_to_three_decimals()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var unknown = $"UT-ASNQ-N-{Guid.NewGuid().ToString("N")[..6]}";   // never created in any master
        var setup = await SeedPoWithUnitAsync(unknown, decimals: null);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var four = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 1.2345m));
        four.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(four));
        (await Read<AsnDetailDto>(four)).Errors.Should().Equal(new[] { ScaleMessage(setup, unknown, 3) });

        var three = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 1.234m));
        three.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(three));
        (await Read<AsnDetailDto>(three)).Data!.Lines.Single().UnitDecimalPlaces.Should().Be(3,
            because: "an unknown unit reads with the fallback precision");
    }

    // the same rule on the PUT path, and the GET-by-id DTO carries the precision too.
    [SkippableFact]
    public async Task ShipQty_precision_is_enforced_on_update_and_the_detail_dto_carries_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await SeedPoWithUnitAsync(ZeroDecimalsUnit, decimals: 0);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 3m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        UpdateAsnRequest Update(decimal qty) => new(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest> { new(setup.PoLineId, ShippedQty: qty, BatchNumber: null, ExpiryDate: null) });

        var bad = await client.PutAsJsonAsync($"/api/asns/{asnId}", Update(1.5m));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(bad));
        (await Read<AsnDetailDto>(bad)).Errors.Should().Equal(new[] { ScaleMessage(setup, ZeroDecimalsUnit, 0) });

        var ok = await client.PutAsJsonAsync($"/api/asns/{asnId}", Update(4m));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));
        (await Read<AsnDetailDto>(ok)).Data!.Lines.Single().UnitDecimalPlaces.Should().Be(0);

        // GetAsnById builds its DTO WITHOUT the injected resolver — it must still carry the precision.
        var fetched = await client.GetAsync($"/api/asns/{asnId}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(fetched));
        var fetchedLine = (await Read<AsnDetailDto>(fetched)).Data!.Lines.Single();
        fetchedLine.ShippedQty.Should().Be(4m, because: "the rejected 1.5 left the stored line untouched; the accepted 4 replaced it");
        fetchedLine.UnitDecimalPlaces.Should().Be(0);
    }

    // ── AsnShipQtyScale, in isolation (no DB): the scale test, the per-line KEY, and the one-call batching ─────
    // The 400 body carries bare messages (GlobalExceptionHandler flattens a ValidationException), so the
    // lines[i].shippedQty key is only observable in-process — asserted here against the helper itself.

    [Theory]
    [InlineData("2.5", 0, true)]
    [InlineData("0.5", 0, true)]
    [InlineData("2", 0, false)]
    [InlineData("2.0", 0, false)]
    [InlineData("2.000", 0, false)]          // trailing zeros are not decimals
    [InlineData("1.25", 2, false)]           // exactly n
    [InlineData("1.250", 2, false)]
    [InlineData("1.255", 2, true)]           // n + 1
    [InlineData("1.5", 1, false)]
    [InlineData("1.55", 1, true)]
    [InlineData("0.001", 3, false)]
    [InlineData("0.0001", 3, true)]
    [InlineData("79228162514264337593543950335", 3, false)]   // decimal.MaxValue: qty * 10^n would overflow
    [InlineData("79228162514264337593543950335", 0, false)]
    [InlineData("1.5", -2, true)]            // a corrupt negative precision behaves as 0
    [InlineData("1.0000000000000000000000000001", 99, false)]  // beyond decimal's 28 places is clamped, never throws
    public void ExceedsScale_judges_the_numeric_value_not_the_stored_scale(string qty, int places, bool exceeds)
        => AsnShipQtyScale.ExceedsScale(decimal.Parse(qty, CultureInfo.InvariantCulture), places).Should().Be(exceeds);

    private static readonly Guid PoA = Guid.NewGuid();
    private static readonly Guid PoB = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string> PoNumbers =
        new Dictionary<Guid, string> { [PoA] = "PUR000359", [PoB] = "PUR000360" };

    [Fact]
    public async Task Offending_lines_are_keyed_by_submitted_index_and_the_message_names_the_po_line()
    {
        var ea = PoLineWithUnit("EA", position: 10, PoA);
        var kg = PoLineWithUnit("KG", position: 20, PoA);
        var strange = PoLineWithUnit("ZZ-UNKNOWN", position: 30, PoB);
        var blank = PoLineWithUnit("", position: 40, PoB);
        var poLines = new[] { ea, kg, strange, blank }.ToDictionary(l => l.Id);
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["EA"] = 0, ["KG"] = 3 });

        var lines = new List<CreateAsnLineRequest>
        {
            new(ea.Id, 2.5m, null, null),        // [0] EA allows 0            → error
            new(kg.Id, 1.2345m, null, null),     // [1] KG allows 3            → error
            new(ea.Id, 4m, null, null),          // [2] fine
            new(strange.Id, 1.2345m, null, null),// [3] unknown → fallback 3   → error
            new(strange.Id, 1.234m, null, null), // [4] fine
            new(blank.Id, 1.2345m, null, null),  // [5] blank → fallback 3     → error, no "for unit"
            new(kg.Id, 1m, null, null),          // [6] fine
        };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => AsnShipQtyScale.EnsureAsync(units, lines, poLines, PoNumbers, CancellationToken.None));

        ex.Errors.Keys.Should().BeEquivalentTo(new[]
        {
            "lines[0].shippedQty", "lines[1].shippedQty", "lines[3].shippedQty", "lines[5].shippedQty",
        });
        ex.Errors["lines[0].shippedQty"].Should().Equal("Ship quantity on PUR000359 line 10 for unit EA allows at most 0 decimal places.");
        ex.Errors["lines[1].shippedQty"].Should().Equal("Ship quantity on PUR000359 line 20 for unit KG allows at most 3 decimal places.");
        ex.Errors["lines[3].shippedQty"].Should().Equal("Ship quantity on PUR000360 line 30 for unit ZZ-UNKNOWN allows at most 3 decimal places.");
        ex.Errors["lines[5].shippedQty"].Should().Equal("Ship quantity on PUR000360 line 40 allows at most 3 decimal places.");
    }

    [Fact]
    public async Task A_precision_of_one_reads_in_the_singular_and_valid_lines_do_not_throw()
    {
        var litre = PoLineWithUnit("L1", position: 10, PoA);
        var poLines = new[] { litre }.ToDictionary(l => l.Id);
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["L1"] = 1 });

        var ex = await Assert.ThrowsAsync<ValidationException>(() => AsnShipQtyScale.EnsureAsync(
            units, new List<CreateAsnLineRequest> { new(litre.Id, 0.25m, null, null) }, poLines, PoNumbers, CancellationToken.None));
        ex.Errors["lines[0].shippedQty"].Should().Equal("Ship quantity on PUR000359 line 10 for unit L1 allows at most 1 decimal place.");

        await AsnShipQtyScale.EnsureAsync(
            units, new List<CreateAsnLineRequest> { new(litre.Id, 0.2m, null, null), new(litre.Id, 7m, null, null) },
            poLines, PoNumbers, CancellationToken.None);   // completes: every line is within its precision
    }

    [Fact]
    public async Task A_po_number_that_is_not_known_drops_out_of_the_message_but_the_line_stays_named()
    {
        var ea = PoLineWithUnit("EA", position: 10, PoA);
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["EA"] = 0 });

        var ex = await Assert.ThrowsAsync<ValidationException>(() => AsnShipQtyScale.EnsureAsync(
            units, new List<CreateAsnLineRequest> { new(ea.Id, 0.5m, null, null) },
            new Dictionary<Guid, PurchaseOrderLine> { [ea.Id] = ea },
            new Dictionary<Guid, string>(), CancellationToken.None));

        ex.Errors["lines[0].shippedQty"].Should().Equal("Ship quantity on line 10 for unit EA allows at most 0 decimal places.");
    }

    [Fact]
    public async Task Every_unit_on_the_request_is_resolved_in_one_batched_call()
    {
        var ea = PoLineWithUnit("EA", position: 10, PoA);
        var kg = PoLineWithUnit("KG", position: 20, PoA);
        var m = PoLineWithUnit("M", position: 30, PoB);
        var poLines = new[] { ea, kg, m }.ToDictionary(l => l.Id);
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["EA"] = 0, ["KG"] = 3, ["M"] = 2 });

        await AsnShipQtyScale.EnsureAsync(
            units,
            new List<CreateAsnLineRequest> { new(ea.Id, 1m, null, null), new(kg.Id, 1m, null, null), new(m.Id, 1m, null, null), new(ea.Id, 2m, null, null) },
            poLines, PoNumbers, CancellationToken.None);

        units.BatchCalls.Should().Be(1, because: "one resolver query per request, however many lines it carries");
        units.SingleCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_line_whose_po_line_is_unknown_is_skipped_not_thrown_on()
    {
        // CreateAsn / UpdateAsn report unknown PurchaseOrderLineIds themselves, before this rule runs.
        var known = PoLineWithUnit("EA", position: 10, PoA);
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["EA"] = 0 });

        await AsnShipQtyScale.EnsureAsync(
            units,
            new List<CreateAsnLineRequest> { new(Guid.NewGuid(), 9.99m, null, null), new(known.Id, 1m, null, null) },
            new Dictionary<Guid, PurchaseOrderLine> { [known.Id] = known }, PoNumbers, CancellationToken.None);
    }

    // The core overload is what CreateAsnFromSchedule drives: it reports under whatever key each line carries, in one
    // batched resolver call, and an empty request never touches the resolver.
    [Fact]
    public async Task The_core_overload_reports_under_each_lines_own_key_and_skips_the_resolver_when_empty()
    {
        var units = new StubUnitDecimals(new Dictionary<string, int> { ["EA"] = 0, ["KG"] = 3 });

        var ex = await Assert.ThrowsAsync<ValidationException>(() => AsnShipQtyScale.EnsureAsync(
            units,
            new List<AsnShipQtyScale.Line>
            {
                new("lines[2].shippedQty", "PUR000359", 10, "EA", 1.5m),     // over: EA allows 0
                new("lines[0].shippedQty", "PUR000360", 20, "KG", 1.234m),   // fine
                new("lines[1].shippedQty", "PUR000360", 30, "KG", 1.2345m),  // over: KG allows 3
            },
            CancellationToken.None));

        ex.Errors.Keys.Should().BeEquivalentTo(new[] { "lines[2].shippedQty", "lines[1].shippedQty" });
        ex.Errors["lines[2].shippedQty"].Should().Equal("Ship quantity on PUR000359 line 10 for unit EA allows at most 0 decimal places.");
        ex.Errors["lines[1].shippedQty"].Should().Equal("Ship quantity on PUR000360 line 30 for unit KG allows at most 3 decimal places.");
        units.BatchCalls.Should().Be(1);

        await AsnShipQtyScale.EnsureAsync(units, new List<AsnShipQtyScale.Line>(), CancellationToken.None);
        units.BatchCalls.Should().Be(1, because: "an empty request has nothing to resolve");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a fresh tagged PO (qty 100) and re-points its single line at <paramref name="unitCode"/>. When
    /// <paramref name="decimals"/> is given the unit is created (idempotently) with exactly that precision; null leaves the
    /// code OUT of the unit master so it resolves to the fallback.
    /// </summary>
    private async Task<ProcureToPayFlow.Setup> SeedPoWithUnitAsync(string unitCode, int? decimals)
    {
        if (decimals is { } d) await UnitMasterDecimalsTests.EnsureUnitAsync(_fx, unitCode, d);

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 100m);
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == setup.PoLineId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderUnit, unitCode));
        return setup;
    }

    private async Task<int> AsnLineCountAsync(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AsnLines.IgnoreQueryFilters().CountAsync(l => l.PurchaseOrderLineId == poLineId && !l.IsDeleted);
    }

    private static PurchaseOrderLine PoLineWithUnit(string unit, int position, Guid poId)
        => new() { Id = Guid.NewGuid(), OrderUnit = unit, PositionNo = position, PurchaseOrderId = poId };

    /// <summary>Fixed-answer resolver that counts how it is called (case-insensitive, unknown → 3, like the real one).</summary>
    private sealed class StubUnitDecimals(IReadOnlyDictionary<string, int> known) : IUnitDecimalsResolver
    {
        public int BatchCalls { get; private set; }
        public int SingleCalls { get; private set; }
        public int Fallback => 3;

        public Task<int> ResolveAsync(string? unitCode, CancellationToken ct)
        {
            SingleCalls++;
            return Task.FromResult(!string.IsNullOrWhiteSpace(unitCode) && known.TryGetValue(unitCode.Trim(), out var p) ? p : Fallback);
        }

        public Task<IReadOnlyDictionary<string, int>> ResolveAsync(IEnumerable<string?> unitCodes, CancellationToken ct)
        {
            BatchCalls++;
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in unitCodes)
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                result[code] = known.TryGetValue(code.Trim(), out var p) ? p : Fallback;
            }
            return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
        }
    }

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
