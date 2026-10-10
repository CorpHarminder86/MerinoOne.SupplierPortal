using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// The PO-negotiation detail read, on REAL SQL through the real host. The stored negotiation is still delta-only (only
/// the lines a supplier changed are persisted), but the read now returns the WHOLE order so a reviewer sees the lines
/// that were left alone beside the ones that moved:
/// <list type="bullet">
///   <item>one DTO per PO line, ordered by position then sequence — the unchanged ones synthesised from the live PO
///     line (original == negotiated, <c>IsChanged</c> false), the changed one carrying its stored original → proposed
///     values (<c>IsChanged</c> true);</item>
///   <item>every line carries the PO line's item description and order unit, and that unit's decimal places from the
///     unit master (3 when the unit is not in it);</item>
///   <item>visibility is unchanged: the supplier who raised it reads it, another supplier's user gets 404, and a
///     reviewer (buyer) reads every negotiation in the tenant.</item>
/// </list>
/// The unit-master rows and the extra PO lines are created per test with unique codes — the test DB persists between runs.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoNegotiationDetailQueryTests
{
    private const string NegotiationsUrl = "/api/purchase-orders/negotiations";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PoNegotiationDetailQueryTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Detail_returns_every_PO_line_in_position_order_with_only_the_negotiated_line_flagged_changed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedThreeLinePoAsync();
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // The supplier moves line 2 only. Lines 1 and 3 are re-sent verbatim (as the editor does) and dropped as no-ops.
        var proposedQty = 17.25m;
        var proposedDate = po.PoDate.AddDays(21).AddHours(9).AddMinutes(30);
        var negotiationId = await RaiseNegotiationAsync(client, po, proposedQty, proposedDate);

        var resp = await client.GetAsync($"{NegotiationsUrl}/{negotiationId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var dto = (await Read<PoNegotiationDto>(resp)).Data!;

        dto.Id.Should().Be(negotiationId);
        dto.PurchaseOrderId.Should().Be(po.Setup.PoId);
        dto.NegotiationStatus.Should().Be(nameof(PoNegotiationStatus.Submitted));

        // All three lines, in position-then-sequence order. The extra PO lines were inserted out of order, so an
        // unsorted read (database order = insertion order) would come back (10,1) (20,2) (20,1).
        dto.Lines.Select(l => (l.PositionNo, l.SequenceNo)).Should().Equal(
            new[] { (10, 1), (20, 1), (20, 2) },
            because: "the whole order is returned, sorted by position then sequence");
        var (first, second, third) = (dto.Lines[0], dto.Lines[1], dto.Lines[2]);

        // The changed line: stored original → proposed values.
        second.PurchaseOrderLineId.Should().Be(po.Second.Id);
        second.IsChanged.Should().BeTrue();
        second.OriginalQty.Should().Be(po.Second.OrderQty);
        second.NegotiatedQty.Should().Be(proposedQty);
        second.OriginalDeliveryDate.Should().Be(po.Second.DeliveryDate);
        second.NegotiatedDeliveryDate.Should().Be(proposedDate);
        second.OriginalPrice.Should().Be(po.Second.PriceUnit);
        second.NegotiatedPrice.Should().Be(po.Second.PriceUnit, because: "price is not negotiable — the client echoes the PO's");

        // The untouched lines: original == negotiated == the live PO line.
        AssertUnchanged(first, po.First);
        AssertUnchanged(third, po.Third);
        third.OriginalDeliveryDate.Should().BeNull("a line with no delivery date stays null on both sides");
        third.NegotiatedDeliveryDate.Should().BeNull();

        // Display context on EVERY line (changed or not): description + unit from the PO line.
        dto.Lines.Should().OnlyContain(l => !string.IsNullOrWhiteSpace(l.ItemDescription) && !string.IsNullOrWhiteSpace(l.OrderUnit));
        (first.ItemDescription, first.OrderUnit).Should().Be((po.First.Description, po.First.Unit));
        (second.ItemDescription, second.OrderUnit).Should().Be((po.Second.Description, po.Second.Unit));
        (third.ItemDescription, third.OrderUnit).Should().Be((po.Third.Description, po.Third.Unit));

        // Unit decimals come from the unit master, per line: a unit that is not in it falls back to 3, and the two
        // seeded units (5 and 0) prove the value is really looked up rather than defaulted.
        first.UnitDecimalPlaces.Should().Be(3, because: "the order unit is not in the unit master");
        second.UnitDecimalPlaces.Should().Be(5);
        third.UnitDecimalPlaces.Should().Be(0);

        // The stored model is unchanged: still ONE persisted line, the one that moved.
        (await StoredLinesAsync(po.Setup.PoId)).Should().ContainSingle()
            .Which.PurchaseOrderLineId.Should().Be(po.Second.Id);
    }

    [SkippableFact]
    public async Task The_supplier_who_raised_it_reads_it_and_another_suppliers_user_gets_404()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedThreeLinePoAsync();
        var creator = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var negotiationId = await RaiseNegotiationAsync(creator, po, 17.25m, po.PoDate.AddDays(21));

        var own = await creator.GetAsync($"{NegotiationsUrl}/{negotiationId}");
        own.StatusCode.Should().Be(HttpStatusCode.OK, because: await own.Content.ReadAsStringAsync());

        // A supplier-role user whose only SecRight is on some OTHER supplier's seccode: it passes the endpoint's
        // PurchaseOrder.Read policy, so the 404 is the seccode row-level security hiding the negotiation, not a 403.
        var stranger = await _fx.ClientAsAsync(await SeedOtherSupplierUserAsync(), IntegrationTestFixture.CompanyId);
        var theirs = await stranger.GetAsync($"{NegotiationsUrl}/{negotiationId}");

        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound, because: await theirs.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task A_reviewer_reads_the_whole_order_too()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedThreeLinePoAsync();
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var negotiationId = await RaiseNegotiationAsync(supplier, po, 17.25m, po.PoDate.AddDays(21));

        // The buyer holds PurchaseOrder.ApproveNegotiation, so it takes the tenant-wide bypass path in the handler —
        // and the PO lines it widens to must not depend on the supplier's seccode.
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var resp = await buyer.GetAsync($"{NegotiationsUrl}/{negotiationId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var dto = (await Read<PoNegotiationDto>(resp)).Data!;
        dto.Lines.Select(l => (l.PositionNo, l.SequenceNo, l.IsChanged)).Should().Equal(
            new[] { (10, 1, false), (20, 1, true), (20, 2, false) });
    }

    // ───────────────────────────── arrange ─────────────────────────────

    /// <summary>A PO line as it was written to the database — the expected "original" side of every assertion.</summary>
    private sealed record Line(
        Guid Id, int PositionNo, int SequenceNo, string ItemCode, string Description, string Unit,
        decimal OrderQty, DateTime? DeliveryDate, decimal PriceUnit);

    private sealed record ThreeLinePo(ProcureToPayFlow.Setup Setup, DateTime PoDate, Line First, Line Second, Line Third);

    /// <summary>
    /// A Released PO with three lines (10/1, 20/1, 20/2) in three different units: one that is not in the unit
    /// master, one with 5 decimal places and one with 0. The seeded single line becomes 10/1; the other two are inserted
    /// OUT of position order so only a real sort puts them right.
    /// </summary>
    private async Task<ThreeLinePo> SeedThreeLinePoAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var fiveDecimalUnit = await SeedUnitAsync(tag, 5);
        var zeroDecimalUnit = await SeedUnitAsync(tag, 0);
        var unknownUnit = $"NOUNIT-{tag}";   // deliberately absent from the unit master

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, priceUnit: 100m, confirm: false);
        var poDate = await PoDateAsync(setup.PoId);

        var first = new Line(setup.PoLineId, 10, 1, setup.ItemCode, $"Widget A {tag}", unknownUnit, 10m, poDate.AddDays(7), 100m);
        var second = new Line(Guid.NewGuid(), 20, 1, $"NQ-B-{tag}", $"Widget B {tag}", fiveDecimalUnit, 20m, poDate.AddDays(14), 55.5m);
        var third = new Line(Guid.NewGuid(), 20, 2, $"NQ-C-{tag}", $"Widget C {tag}", zeroDecimalUnit, 30m, null, 70m);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => l.Id == first.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ItemDescription, first.Description)
                .SetProperty(l => l.OrderUnit, first.Unit)
                .SetProperty(l => l.DeliveryDate, first.DeliveryDate));

        foreach (var l in new[] { third, second })   // 20/2 first, then 20/1 — one save each so insertion order is exact
        {
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                Id = l.Id, PurchaseOrderId = setup.PoId, PositionNo = l.PositionNo, SequenceNo = l.SequenceNo,
                ItemCode = l.ItemCode, ItemDescription = l.Description, OrderUnit = l.Unit,
                OrderQty = l.OrderQty, PriceUnit = l.PriceUnit, Price = l.PriceUnit * l.OrderQty,
                DeliveryDate = l.DeliveryDate, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new ThreeLinePo(setup, poDate, first, second, third);
    }

    /// <summary>
    /// A unit-master row with exactly <paramref name="places"/> decimal places, under the fixture company, with a code
    /// unique to this call. The value is pinned with an explicit UPDATE and read back: unit.decimalPlaces carries a
    /// database default of 2, and EF can omit a CLR-default 0 from the INSERT and let that default land instead — the pin
    /// makes the arranged value independent of that.
    /// </summary>
    private async Task<string> SeedUnitAsync(string tag, int places)
    {
        var id = Guid.NewGuid();
        var code = $"UT-NQ{places}-{tag}";
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Units.Add(new UnitEntity
        {
            Id = id, Code = code, Description = "PO negotiation detail test unit", UnitType = UnitType.Quantity,
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

    /// <summary>
    /// A login-able Supplier-role user whose ONLY row-level right is on a different supplier's seccode — i.e. a user of
    /// some other supplier. Returns its user code.
    /// </summary>
    private async Task<string> SeedOtherSupplierUserAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var userCode = $"nego-other-{tag}";
        await _fx.CreateSupplierAsync($"other{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: userCode, canWrite: false);

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var supplierRoleId = await db.Roles.IgnoreQueryFilters()
            .Where(r => r.Name == RoleNames.Supplier).Select(r => r.Id).SingleAsync();

        db.AppUsers.Add(new AppUser
        {
            Id = userId, UserCode = userCode, FullName = userCode, Email = $"{userCode}@merino.local",
            PasswordHash = PasswordHasher.DeterministicHash(SecurityTestHarness.Password),
            IsInternal = false, IsMfaEnabled = false, IsActive = true,
            TenantId = IntegrationTestFixture.TenantId, CreatedBy = "seed", CreatedOn = now,
        });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(), AppUserId = userId, RoleId = supplierRoleId, CreatedBy = "seed", CreatedOn = now,
        });
        db.UserCompanyMaps.Add(new UserCompanyMap
        {
            Id = Guid.NewGuid(), AppUserId = userId, TenantEntityId = IntegrationTestFixture.CompanyId,
            TenantId = IntegrationTestFixture.TenantId, IsDefault = true, AllSuppliers = false,
            CreatedBy = "seed", CreatedOn = now,
        });
        await db.SaveChangesAsync();
        return userCode;
    }

    /// <summary>Raises the negotiation through the real POST and returns the new id read from the create response.</summary>
    private async Task<Guid> RaiseNegotiationAsync(HttpClient supplier, ThreeLinePo po, decimal secondQty, DateTime secondDate)
    {
        var request = new CreatePoNegotiationRequest(po.Setup.PoId, "line two moves", new()
        {
            new PoNegotiationLineInput(po.First.Id, po.First.OrderQty, po.First.DeliveryDate, po.First.PriceUnit),
            new PoNegotiationLineInput(po.Second.Id, secondQty, secondDate, po.Second.PriceUnit),
            new PoNegotiationLineInput(po.Third.Id, po.Third.OrderQty, po.Third.DeliveryDate, po.Third.PriceUnit),
        });

        var resp = await supplier.PostAsJsonAsync(NegotiationsUrl, request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());

        // The create response carries the new negotiation's id — the client needs it to open the detail.
        var created = (await Read<PoNegotiationDto>(resp)).Data!;
        created.Id.Should().NotBeEmpty();
        return created.Id;
    }

    // ───────────────────────────── assert / read ─────────────────────────────

    private static void AssertUnchanged(PoNegotiationLineDto actual, Line expected)
    {
        actual.PurchaseOrderLineId.Should().Be(expected.Id);
        actual.ItemCode.Should().Be(expected.ItemCode);
        actual.IsChanged.Should().BeFalse(because: "the supplier left this line alone");
        actual.OriginalQty.Should().Be(expected.OrderQty);
        actual.NegotiatedQty.Should().Be(expected.OrderQty);
        actual.OriginalDeliveryDate.Should().Be(expected.DeliveryDate);
        actual.NegotiatedDeliveryDate.Should().Be(expected.DeliveryDate);
        actual.OriginalPrice.Should().Be(expected.PriceUnit);
        actual.NegotiatedPrice.Should().Be(expected.PriceUnit);
    }

    private async Task<List<PurchaseOrderNegotiationLine>> StoredLinesAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderNegotiationLines.IgnoreQueryFilters()
            .Where(l => l.PurchaseOrderNegotiation!.PurchaseOrderId == poId)
            .ToListAsync();
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
}
