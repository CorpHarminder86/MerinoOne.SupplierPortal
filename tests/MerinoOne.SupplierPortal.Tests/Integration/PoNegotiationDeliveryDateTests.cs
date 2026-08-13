using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Feedback 2026-08-12 (2) + (3) — the negotiation line rules, on REAL SQL through the real host:
/// <list type="bullet">
///   <item>A proposed delivery instant EARLIER than the PO date is rejected (400). The UI enforces the same rule
///     twice over — the picker's <c>min</c> and a pre-POST check — and both are trivially bypassable, so this
///     handler check is the actual authority.</item>
///   <item>A later delivery instant is accepted and stored to the MINUTE. Delivery became a date+time field in
///     the same change; a handler that silently truncated the time would make the whole feature a no-op.</item>
///   <item>Price is no longer negotiable: the client sends the PO's own price unit, so no price delta is ever
///     persisted even though the request contract still carries the field.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoNegotiationDeliveryDateTests
{
    private const string NegotiationsUrl = "/api/purchase-orders/negotiations";

    private readonly IntegrationTestFixture _fx;
    public PoNegotiationDeliveryDateTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Delivery_before_the_PO_date_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // confirm:false leaves the PO Released, which is what the negotiate gate requires.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var poDate = await PoDateAsync(setup.PoId);

        var request = new CreatePoNegotiationRequest(setup.PoId, "earlier than the order itself", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, setup.OrderQty, poDate.AddMinutes(-1), setup.PriceUnit),
        });

        var resp = await client.PostAsJsonAsync(NegotiationsUrl, request);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: await resp.Content.ReadAsStringAsync());

        // One minute is enough: the boundary is the failure mode, not a wild date. And nothing may be persisted —
        // a rejected negotiation that still flipped the PO to Negotiation would strand it.
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PurchaseOrderNegotiations.IgnoreQueryFilters().AnyAsync(n => n.PurchaseOrderId == setup.PoId))
            .Should().BeFalse(because: "the guard rejects before any write");
    }

    [SkippableFact]
    public async Task Delivery_after_the_PO_date_is_accepted_and_keeps_its_time()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var poDate = await PoDateAsync(setup.PoId);

        // A deliberately non-midnight instant: the point of the change is that a TIME survives the round trip.
        var proposed = poDate.AddDays(3).AddHours(9).AddMinutes(30);

        var request = new CreatePoNegotiationRequest(setup.PoId, "revised slot", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, setup.OrderQty, proposed, setup.PriceUnit),
        });

        var resp = await client.PostAsJsonAsync(NegotiationsUrl, request);

        resp.IsSuccessStatusCode.Should().BeTrue(because: await resp.Content.ReadAsStringAsync());

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PurchaseOrderNegotiationLines.IgnoreQueryFilters()
            .SingleAsync(l => l.PurchaseOrderNegotiation!.PurchaseOrderId == setup.PoId);

        line.NegotiatedDeliveryDate.Should().Be(proposed, because: "the minute the supplier chose is the proposal");

        // Feedback (2): the client sends the PO's own price, so no price delta is written. Both sides are pinned
        // to the seeded PriceUnit — asserting only that they equal EACH OTHER would also hold if both were zero.
        line.OriginalPrice.Should().Be(setup.PriceUnit);
        line.NegotiatedPrice.Should().Be(setup.PriceUnit);
    }

    [SkippableFact]
    public async Task Delivery_exactly_equal_to_the_PO_instant_is_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var poDate = await PoDateAsync(setup.PoId);

        // The rule is delivery >= PO date, so the boundary itself is legal. Pinned because the guard is written
        // as `proposed < po.PoDate`: flipping it to `<=` during a future edit would silently make the boundary
        // illegal, and nothing else would notice.
        var request = new CreatePoNegotiationRequest(setup.PoId, "exactly on the PO instant", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, setup.OrderQty, poDate, setup.PriceUnit),
        });

        var resp = await client.PostAsJsonAsync(NegotiationsUrl, request);

        resp.IsSuccessStatusCode.Should().BeTrue(because: await resp.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Qty_only_change_is_allowed_on_a_line_whose_existing_delivery_date_predates_the_PO()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var poDate = await PoDateAsync(setup.PoId);

        // Put the LINE's existing delivery instant BEFORE the PO date — the shape that exposed the bug. ERP data
        // can carry this (poDate is a real time-of-day while a same-day line date arrives at midnight).
        var stale = poDate.AddHours(-2);
        await using (var arrange = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = arrange.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.Id == setup.PoLineId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.DeliveryDate, stale));
        }

        // The supplier edits ONLY the quantity. The editor re-sends the untouched delivery instant verbatim, so
        // the request carries a date that predates the PO — but the supplier never proposed it, and the floor
        // must not fault it. Before the fix this returned 400 and the quantity change was unsubmittable.
        var request = new CreatePoNegotiationRequest(setup.PoId, "quantity only", new()
        {
            new PoNegotiationLineInput(setup.PoLineId, setup.OrderQty - 2m, stale, setup.PriceUnit),
        });

        var resp = await client.PostAsJsonAsync(NegotiationsUrl, request);

        resp.IsSuccessStatusCode.Should().BeTrue(because: await resp.Content.ReadAsStringAsync());

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var check = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await check.PurchaseOrderNegotiationLines.IgnoreQueryFilters()
            .SingleAsync(l => l.PurchaseOrderNegotiation!.PurchaseOrderId == setup.PoId);

        line.NegotiatedQty.Should().Be(setup.OrderQty - 2m);
        line.NegotiatedDeliveryDate.Should().Be(stale, because: "the untouched date is carried through, not rejected");
    }

    private async Task<DateTime> PoDateAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters()
            .Where(p => p.Id == poId).Select(p => p.PoDate).SingleAsync();
    }
}
