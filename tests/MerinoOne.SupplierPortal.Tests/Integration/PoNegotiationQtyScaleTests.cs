using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// The negotiated-quantity scale rule, on REAL SQL through the real host: a PROPOSED quantity may not carry more decimal
/// places than the line's unit of measure allows (<c>mdm.Unit.decimalPlaces</c>; a unit missing from the master allows 3).
/// <list type="bullet">
///   <item>Too many decimals → 400, keyed to the offending line of the request (and naming its PO line in the message,
///     the only part a client sees), and NOTHING is written — the PO is not flipped to Negotiation either, which would
///     strand it.</item>
///   <item>Exactly the allowed number of decimals is accepted, and trailing zeros are not decimals (7.00 is a whole number).</item>
///   <item>A quantity the supplier did not change is never judged: the editor re-sends it verbatim, and ERP-fed order
///     quantities are not guaranteed to fit the unit master.</item>
/// </list>
/// Every test builds its own unit-master row (unique code) and PO — the test DB persists between runs.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoNegotiationQtyScaleTests
{
    private const string NegotiationsUrl = "/api/purchase-orders/negotiations";

    /// <summary>ProcureToPayFlow pushes its single line as sequence 1; the lines added here use it too.</summary>
    private const int SeededSequenceNo = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PoNegotiationQtyScaleTests(IntegrationTestFixture fx) => _fx = fx;

    // ───────────────────────────── rejection ─────────────────────────────

    [SkippableFact]
    public async Task A_quantity_with_more_decimals_than_the_unit_allows_is_rejected_and_nothing_is_persisted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, unit) = await SeedPoInUnitAsync(places: 0);
        var client = await SupplierClientAsync();

        var resp = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 7.5m));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await resp.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(resp)).Errors.Should().Equal(
            ScaleMessage(setup.PoPositionNo, SeededSequenceNo, unit, places: 0));
        await AssertNothingPersistedAsync(setup);
    }

    [SkippableFact]
    public async Task The_rejection_is_keyed_to_the_offending_line_of_the_request()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, unit) = await SeedPoInUnitAsync(places: 0);
        var second = await AddLineAsync(setup.PoId, positionNo: 20, unit);
        var first = await PoLineAsync(setup.PoLineId);

        // [0] is untouched and dropped as a no-op, so by the time [1] is judged ZERO delta lines have been kept: the
        // key must carry the line's index in the REQUEST (1), not its position among the persisted lines (0).
        var request = new CreatePoNegotiationRequest(setup.PoId, "scale", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, setup.OrderQty, first.DeliveryDate, first.PriceUnit),
            new PoNegotiationLineInput(second.Id, 7.5m, null, second.PriceUnit),
        });

        // The HTTP layer flattens a ValidationException to its messages, so the key is only observable on the handler.
        // It reads seccode-protected rows, which the test host's request-less scope cannot see — hence an admin context.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var user = new StubAdmin();
        await using var db = ActivatorUtilities.CreateInstance<AppDbContext>(scope.ServiceProvider, user, new StubCompany());
        var handler = new CreatePoNegotiationCommandHandler(db, user, new SupplierWriteGuard(db, user), new UnitDecimalsResolver(db));

        var act = () => handler.Handle(new CreatePoNegotiationCommand(request), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ValidationException>()).Which;
        thrown.Errors.Keys.Should().Equal("lines[1].negotiatedQty");
        thrown.Errors["lines[1].negotiatedQty"].Should().Equal(ScaleMessage(20, SeededSequenceNo, unit, places: 0));
        await AssertNothingPersistedAsync(setup);
    }

    [SkippableFact]
    public async Task A_unit_missing_from_the_master_allows_three_decimal_places()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, unit) = await SeedPoInUnitAsync(places: null);
        var client = await SupplierClientAsync();

        var tooMany = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 1.2345m));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await tooMany.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(tooMany)).Errors.Should().Equal(
            ScaleMessage(setup.PoPositionNo, SeededSequenceNo, unit, places: 3));
        await AssertNothingPersistedAsync(setup);

        var fits = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 1.234m));
        fits.StatusCode.Should().Be(HttpStatusCode.OK, because: await fits.Content.ReadAsStringAsync());
    }

    // ───────────────────────────── acceptance ─────────────────────────────

    [SkippableFact]
    public async Task A_quantity_with_exactly_the_units_decimal_places_is_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, unit) = await SeedPoInUnitAsync(places: 2);
        var client = await SupplierClientAsync();

        // One decimal too many is rejected — and, being a 400, leaves the PO Released so the next call can still raise it.
        var tooMany = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 12.345m));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await tooMany.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(tooMany)).Errors.Should().Equal(
            ScaleMessage(setup.PoPositionNo, SeededSequenceNo, unit, places: 2));
        await AssertNothingPersistedAsync(setup);

        // Exactly two is the boundary and is legal.
        var exact = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 12.34m));
        exact.StatusCode.Should().Be(HttpStatusCode.OK, because: await exact.Content.ReadAsStringAsync());
        (await StoredLineAsync(setup.PoId)).NegotiatedQty.Should().Be(12.34m);
    }

    [SkippableFact]
    public async Task A_whole_quantity_is_accepted_for_a_zero_decimal_unit_even_when_written_with_trailing_zeros()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, _) = await SeedPoInUnitAsync(places: 0);
        var client = await SupplierClientAsync();

        // 7.00m is a scale-2 decimal — what a numeric field bound to a decimal sends for "7.00" — but it is the whole
        // number 7, which a zero-decimal unit allows. Judging the scale of the representation would wrongly reject it.
        var resp = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 7.00m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        (await StoredLineAsync(setup.PoId)).NegotiatedQty.Should().Be(7m);
    }

    [SkippableFact]
    public async Task A_date_only_change_is_not_rejected_when_the_stored_quantity_has_excess_decimals()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, _) = await SeedPoInUnitAsync(places: 0);
        var client = await SupplierClientAsync();

        // ERP-fed data need not fit the unit master: this whole-number unit carries an order quantity with a decimal.
        await using (var arrange = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = arrange.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.Id == setup.PoLineId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderQty, 10.5m));
        }

        // The supplier moves ONLY the delivery date; the editor re-sends the order quantity verbatim. That quantity is
        // not a proposal, so the scale rule must not fault it.
        var newDate = (await PoDateAsync(setup.PoId)).AddDays(5).AddHours(10);
        var resp = await client.PostAsJsonAsync(NegotiationsUrl, await RequestAsync(setup, 10.5m, newDate));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var stored = await StoredLineAsync(setup.PoId);
        stored.NegotiatedQty.Should().Be(10.5m, because: "the untouched quantity is carried through, not rejected");
        stored.NegotiatedDeliveryDate.Should().Be(newDate);
    }

    // ───────────────────────────── arrange ─────────────────────────────

    private async Task<HttpClient> SupplierClientAsync()
        => await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

    /// <summary>
    /// A Released single-line PO whose line is in a fresh unit with <paramref name="places"/> decimal places — or, for
    /// <c>null</c>, in a unit code that is not in the unit master at all.
    /// </summary>
    private async Task<(ProcureToPayFlow.Setup Setup, string Unit)> SeedPoInUnitAsync(int? places)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var unit = places is { } p ? await SeedUnitAsync(tag, p) : $"NOUNIT-{tag}";

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, priceUnit: 100m, confirm: false);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => l.Id == setup.PoLineId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderUnit, unit));
        return (setup, unit);
    }

    /// <summary>
    /// A unit-master row with exactly <paramref name="places"/> decimal places, under the fixture company, with a code
    /// unique to this call. The value is pinned with an explicit UPDATE and read back: unit.decimalPlaces carries a
    /// database default of 2, and EF can omit a CLR-default 0 from the INSERT and let that default land instead — without
    /// the pin a "0-decimal" unit could silently be a 2-decimal one and these tests would prove nothing.
    /// </summary>
    private async Task<string> SeedUnitAsync(string tag, int places)
    {
        var id = Guid.NewGuid();
        var code = $"UT-NQ{places}-{tag}";
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Units.Add(new UnitEntity
        {
            Id = id, Code = code, Description = "PO negotiation scale test unit", UnitType = UnitType.Quantity,
            DecimalPlaces = places, ConversionFactor = 1m, IsActive = true,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await db.Units.IgnoreQueryFilters().Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DecimalPlaces, places));

        // Read it back: an arranged unit that silently kept the database default would make every assertion below vacuous.
        (await db.Units.IgnoreQueryFilters().Where(u => u.Id == id).Select(u => u.DecimalPlaces).SingleAsync())
            .Should().Be(places, because: "the arranged unit must really carry that many decimal places");
        return code;
    }

    private sealed record AddedLine(Guid Id, decimal PriceUnit);

    /// <summary>A second line on the PO (no delivery date), in the given unit.</summary>
    private async Task<AddedLine> AddLineAsync(Guid poId, int positionNo, string unit)
    {
        var id = Guid.NewGuid();
        const decimal priceUnit = 50m;
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            Id = id, PurchaseOrderId = poId, PositionNo = positionNo, SequenceNo = SeededSequenceNo,
            ItemCode = $"NQ-{id.ToString("N")[..12]}", OrderUnit = unit, OrderQty = 20m,
            PriceUnit = priceUnit, Price = priceUnit * 20m, DeliveryDate = null,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return new AddedLine(id, priceUnit);
    }

    /// <summary>
    /// A one-line request that proposes <paramref name="qty"/> on the seeded line. Like the real editor it re-sends the
    /// line's EXISTING delivery date and unit price verbatim, so only the quantity (and the date, when given) is a proposal.
    /// </summary>
    private async Task<CreatePoNegotiationRequest> RequestAsync(
        ProcureToPayFlow.Setup setup, decimal qty, DateTime? newDeliveryDate = null)
    {
        var line = await PoLineAsync(setup.PoLineId);
        return new CreatePoNegotiationRequest(setup.PoId, "scale rule", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, qty, newDeliveryDate ?? line.DeliveryDate, line.PriceUnit),
        });
    }

    // ───────────────────────────── assert / read ─────────────────────────────

    /// <summary>
    /// The rejection message. It names the PO line (position/sequence), as the delivery-date floor's does: a 400 body
    /// carries messages only — the <c>lines[i].negotiatedQty</c> key never reaches the client — so the message is what
    /// tells the supplier which line to fix.
    /// </summary>
    private static string ScaleMessage(int positionNo, int sequenceNo, string unit, int places)
        => $"Quantity on line {positionNo}/{sequenceNo} for unit {unit} allows at most {places} decimal places.";

    /// <summary>A rejected request writes nothing: no negotiation row, and the PO is still open for a response.</summary>
    private async Task AssertNothingPersistedAsync(ProcureToPayFlow.Setup setup)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.PurchaseOrderNegotiations.IgnoreQueryFilters().AnyAsync(n => n.PurchaseOrderId == setup.PoId))
            .Should().BeFalse(because: "the rule rejects before any write");
        (await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == setup.PoId).Select(p => p.PoStatus).SingleAsync())
            .Should().Be(PoStatus.Released, because: "a rejected request must not flip the PO to Negotiation");
    }

    private async Task<PurchaseOrderNegotiationLine> StoredLineAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderNegotiationLines.IgnoreQueryFilters()
            .SingleAsync(l => l.PurchaseOrderNegotiation!.PurchaseOrderId == poId);
    }

    private async Task<(DateTime? DeliveryDate, decimal PriceUnit)> PoLineAsync(Guid lineId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => l.Id == lineId).Select(l => new { l.DeliveryDate, l.PriceUnit }).SingleAsync();
        return (line.DeliveryDate, line.PriceUnit);
    }

    private async Task<DateTime> PoDateAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters()
            .Where(p => p.Id == poId).Select(p => p.PoDate).SingleAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<Result<T>>(await resp.Content.ReadAsStringAsync(), Json)!;

    // ───────────────────────────── test doubles ─────────────────────────────

    /// <summary>An admin principal in the fixture tenant: privileged at the seccode layer and past the write guard.</summary>
    private sealed class StubAdmin : ICurrentUser
    {
        public string UserCode => "test-admin";
        public string? UserName => "Test Admin";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => true;
        public bool HasPermission(string code) => true;
        public Guid? TenantId => IntegrationTestFixture.TenantId;
        public bool IsPlatformAdmin => false;
    }

    private sealed class StubCompany : ICurrentCompany
    {
        public Guid? ActiveCompanyId => IntegrationTestFixture.CompanyId;
        public IReadOnlyCollection<Guid> AccessibleCompanyIds => new[] { IntegrationTestFixture.CompanyId };
        public bool ActiveCompanyFullAccess => false;
        public Guid? ResolveSource(SharedEndpoint endpoint, Guid? companyId) => companyId;
    }
}
