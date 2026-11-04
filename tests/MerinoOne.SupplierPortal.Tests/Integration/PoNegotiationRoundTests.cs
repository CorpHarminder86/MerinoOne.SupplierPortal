using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R18 (2026-11-03) — PO negotiation ROUNDS (unlimited buyer / supplier ping-pong), on REAL SQL through the real host. The supplier
/// opens round 1; the open round is answered by the party that did not propose it — the buyer approves / rejects / counters a
/// supplier round, the supplier accepts / declines / counters a buyer round — and each counter is a NEW round whose parent flips to
/// Countered. Every test builds its own PO and supplier (the test DB persists between runs). The test host runs the real background
/// workers, so the ERP push is asserted by the existence of its outbox row, never by that row's status.
/// <list type="bullet">
///   <item>Resolution: accept (→ PO Approved + one ERP push for the accepted round only), decline (→ PO back to its pre-negotiation
///     status), counter chains of any length.</item>
///   <item>Who may do what: the policies separate the parties, and a party can neither answer its own round nor a round that was
///     already countered (409). A rejected counter writes nothing.</item>
///   <item>Values: a counter overrides the lines it names, keeps the ones it omits, carries the price, and stores deltas against the LIVE
///     PO line like every round; the create's delivery-floor and quantity-scale rules apply, judged against the round answered.</item>
///   <item>Reads: the supplier sees the buyer's rounds (the copied seccode owner), the detail carries the chain and the previous
///     round's values, the list hides superseded rounds; the SLA nudge chases only the buyer's pending supplier rounds.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoNegotiationRoundTests
{
    private const string NegotiationsUrl = "/api/purchase-orders/negotiations";
    private const string BuyerCounter = "counter";
    private const string SupplierCounter = "supplier-counter";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PoNegotiationRoundTests(IntegrationTestFixture fx) => _fx = fx;

    // ───────────────────────────── resolution ─────────────────────────────

    [SkippableFact]
    public async Task Supplier_accepting_the_buyers_counter_approves_the_PO_and_queues_the_ERP_push_for_the_accepted_round_only()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counter = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));

        var accepted = await PostOkAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{counter.Id}/accept");

        accepted.Id.Should().Be(counter.Id);
        accepted.NegotiationStatus.Should().Be(nameof(PoNegotiationStatus.Approved));

        var acceptedRow = await RoundAsync(counter.Id);
        acceptedRow.NegotiationStatus.Should().Be(PoNegotiationStatus.Approved);
        acceptedRow.ReviewedBy.Should().Be(SecurityTestHarness.Users.Supplier, because: "the accepting party is the one who answered it");
        var supersededRow = await RoundAsync(first.Id);
        supersededRow.NegotiationStatus.Should().Be(PoNegotiationStatus.Countered);
        supersededRow.ReviewedBy.Should().Be(SecurityTestHarness.Users.Buyer);
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Approved);

        // The ERP push is queued under the SAME key shape the buyer's approve uses, for the accepted round alone.
        var pushed = await PushesForAsync(counter.Id);
        var push = pushed.Should().ContainSingle(because: "accepting queues exactly one ERP push").Subject;
        push.DeterministicKey.Should().Be(PushKey(s, counter.Id));
        push.EntityName.Should().Be(OutboxEntity.PurchaseOrder);
        (await PushesForAsync(first.Id)).Should().BeEmpty(because: "a countered round is never pushed to ERP");
    }

    [SkippableFact]
    public async Task Counters_ping_pong_until_the_buyer_approves_a_later_round_and_every_earlier_round_ends_Countered()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));
        var third = await CounterAsync(s.Supplier, SupplierCounter, second.Id, s.Line(7m));

        // The counter responses name the new round and link it to the one it answered.
        second.Should().BeEquivalentTo(new { RoundNo = 2, ProposedBy = "Buyer", ParentNegotiationId = first.Id });
        third.Should().BeEquivalentTo(new { RoundNo = 3, ProposedBy = "Supplier", ParentNegotiationId = second.Id });
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Negotiation, because: "a counter never takes the PO out of Negotiation");

        var approve = await s.Buyer.PostAsync($"{NegotiationsUrl}/{third.Id}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await approve.Content.ReadAsStringAsync());

        var chain = new[] { await RoundAsync(first.Id), await RoundAsync(second.Id), await RoundAsync(third.Id) };
        chain.Select(r => (r.RoundNo, r.ProposedBy, r.NegotiationStatus)).Should().Equal(
            (1, NegotiationParty.Supplier, PoNegotiationStatus.Countered),
            (2, NegotiationParty.Buyer, PoNegotiationStatus.Countered),
            (3, NegotiationParty.Supplier, PoNegotiationStatus.Approved));
        chain.Select(r => r.ParentNegotiationId).Should().Equal(new Guid?[] { null, first.Id, second.Id });
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Approved);

        // Every round stores its deltas against the LIVE PO line (original 10), so the approved round alone carries the whole
        // proposal and the ERP payload needs no knowledge of the rounds before it.
        var line = chain[2].Lines.Should().ContainSingle().Subject;
        (line.OriginalQty, line.NegotiatedQty).Should().Be((10m, 7m));

        // One ERP push — for the round that was approved.
        (await PushesForAsync(third.Id)).Should().ContainSingle();
        (await PushesForAsync(first.Id)).Should().BeEmpty();
        (await PushesForAsync(second.Id)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Supplier_declining_the_buyers_counter_rejects_the_round_and_returns_the_PO_to_its_pre_negotiation_status()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counter = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Negotiation);

        var declined = await PostOkAsync<PoNegotiationDto>(
            s.Supplier, $"{NegotiationsUrl}/{counter.Id}/decline", new DeclinePoNegotiationRequest("  the quantity cannot be met  "));

        declined.NegotiationStatus.Should().Be(nameof(PoNegotiationStatus.Rejected));
        declined.RejectionReason.Should().Be("the quantity cannot be met", because: "the reason is stored trimmed, as a reject's is");

        var row = await RoundAsync(counter.Id);
        row.NegotiationStatus.Should().Be(PoNegotiationStatus.Rejected);
        row.RejectionReason.Should().Be("the quantity cannot be met");
        row.ReviewedBy.Should().Be(SecurityTestHarness.Users.Supplier);
        row.ReviewedAt.Should().NotBeNull();
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Released, because: "the PO returns to the status it had before the negotiation began");
        (await PushesForAsync(counter.Id)).Should().BeEmpty(because: "a declined round is never pushed to ERP");
    }

    [SkippableFact]
    public async Task Two_concurrent_answers_to_one_round_leave_one_winner_and_a_409_for_the_other()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);

        // Whether the two requests interleave (the RowVersion race) or run one after the other (the second finds the round
        // already answered) the outcome is the same: exactly one answer stands and the other is a 409 — never a 500, never both.
        var approve = s.Buyer.PostAsync($"{NegotiationsUrl}/{first.Id}/approve", null);
        var reject = s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/reject", new RejectPoNegotiationRequest("racing the approval"));
        var answers = await Task.WhenAll(approve, reject);

        answers.Select(a => a.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });

        var approvalWon = answers[0].IsSuccessStatusCode;
        (await RoundAsync(first.Id)).NegotiationStatus.Should().Be(approvalWon ? PoNegotiationStatus.Approved : PoNegotiationStatus.Rejected);
        (await PoStatusAsync(s.PoId)).Should().Be(approvalWon ? PoStatus.Approved : PoStatus.Released);
        (await PushesForAsync(first.Id)).Should().HaveCount(approvalWon ? 1 : 0, because: "only an approval queues the ERP push");
    }

    // ───────────────────────────── who may do what ─────────────────────────────

    [SkippableFact]
    public async Task Each_party_can_reach_only_its_own_actions()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counterBody = Counter(s.Line(9m));

        // The buyer's counter needs PurchaseOrder.ApproveNegotiation, which the supplier role does not hold...
        (await s.Supplier.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", counterBody))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // ...and the supplier's three actions need PurchaseOrder.Negotiate, which the buyer role does not hold.
        (await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{SupplierCounter}", counterBody))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await s.Buyer.PostAsync($"{NegotiationsUrl}/{first.Id}/accept", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/decline", new DeclinePoNegotiationRequest("not mine to decline")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await AssertStillOpenAsync(s, first.Id);
    }

    [SkippableFact]
    public async Task A_party_cannot_answer_a_round_it_proposed_itself_or_one_that_was_already_countered()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);

        // The supplier proposed round 1, so the supplier cannot answer it — the buyer does.
        var accept = await s.Supplier.PostAsync($"{NegotiationsUrl}/{first.Id}/accept", null);
        var decline = await s.Supplier.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/decline", new DeclinePoNegotiationRequest("my own round"));
        var supplierCounter = await s.Supplier.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{SupplierCounter}", Counter(s.Line(9m)));
        foreach (var answer in new[] { accept, decline, supplierCounter })
        {
            answer.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await answer.Content.ReadAsStringAsync());
            (await Read<PoNegotiationDto>(answer)).Errors.Should().ContainSingle().Which.Should().Contain("proposed by the Supplier");
        }

        // Once the buyer counters, round 2 is the buyer's own: it cannot approve, reject or counter it...
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));
        var approve = await s.Buyer.PostAsync($"{NegotiationsUrl}/{second.Id}/approve", null);
        var reject = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{second.Id}/reject", new RejectPoNegotiationRequest("my own round"));
        var buyerCounter = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{second.Id}/{BuyerCounter}", Counter(s.Line(8m)));
        foreach (var answer in new[] { approve, reject, buyerCounter })
        {
            answer.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await answer.Content.ReadAsStringAsync());
            (await Read<PoNegotiationDto>(answer)).Errors.Should().ContainSingle().Which.Should().Contain("proposed by the Buyer");
        }

        // ...and round 1, superseded, is no longer open for anyone.
        var lateApprove = await s.Buyer.PostAsync($"{NegotiationsUrl}/{first.Id}/approve", null);
        lateApprove.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Read<PoNegotiationDto>(lateApprove)).Errors.Should().ContainSingle().Which.Should().Contain("Countered");

        (await RoundAsync(second.Id)).NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted, because: "no refused action changed it");
    }

    [SkippableFact]
    public async Task The_supplier_cannot_cancel_a_round_the_buyer_proposed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counter = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));

        var cancel = await s.Supplier.PostAsync($"{NegotiationsUrl}/{counter.Id}/cancel", null);

        cancel.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await cancel.Content.ReadAsStringAsync());
        (await RoundAsync(counter.Id)).NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted,
            because: "a buyer round is answered with decline, not withdrawn");
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Negotiation);

        // The supplier can still withdraw a round it proposed itself — the pre-R18 behaviour is intact.
        var own = await SceneAsync();
        var ownRound = await OpenAsync(own, qty: 8m);
        (await own.Supplier.PostAsync($"{NegotiationsUrl}/{ownRound.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RoundAsync(ownRound.Id)).NegotiationStatus.Should().Be(PoNegotiationStatus.Cancelled);
        (await PoStatusAsync(own.PoId)).Should().Be(PoStatus.Released);
    }

    [SkippableFact]
    public async Task A_supplier_that_may_not_negotiate_cannot_counter_but_can_still_accept()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counter = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));
        await SetAllowNegotiateAsync(s.Setup.Supplier.SupplierId, false);

        var blocked = await s.Supplier.PostAsJsonAsync($"{NegotiationsUrl}/{counter.Id}/{SupplierCounter}", Counter(s.Line(8m)));

        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await blocked.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(blocked)).Errors.Should().ContainSingle().Which.Should().Contain("not permitted to negotiate");
        (await RoundAsync(counter.Id)).NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted);

        // Accepting the buyer's terms raises no counter-proposal, so the toggle does not gate it.
        (await PostOkAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{counter.Id}/accept"))
            .NegotiationStatus.Should().Be(nameof(PoNegotiationStatus.Approved));
    }

    [SkippableFact]
    public async Task Declining_without_a_reason_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var counter = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));

        foreach (var body in new object[] { new { reason = "" }, new { reason = "   " }, new { reason = (string?)null }, new { } })
        {
            var resp = await s.Supplier.PostAsJsonAsync($"{NegotiationsUrl}/{counter.Id}/decline", body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: $"{JsonSerializer.Serialize(body)}: {await resp.Content.ReadAsStringAsync()}");
        }

        (await RoundAsync(counter.Id)).NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted);
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Negotiation);
    }

    // ───────────────────────────── what a counter may say ─────────────────────────────

    [SkippableFact]
    public async Task A_counter_identical_to_the_round_it_answers_is_rejected_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);

        var resp = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", Counter(s.Line(8m)));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await resp.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(resp)).Errors.Should().ContainSingle().Which.Should().Contain("accept the round instead");
        await AssertStillOpenAsync(s, first.Id);
    }

    [SkippableFact]
    public async Task A_counter_identical_to_the_PO_is_rejected_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);

        // Quantity 10 is what the PO already says: the buyer would be proposing nothing — that is a rejection, not a counter.
        var resp = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", Counter(s.Line(s.Setup.OrderQty)));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await resp.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(resp)).Errors.Should().ContainSingle().Which.Should().Contain("identical to the purchase order");
        await AssertStillOpenAsync(s, first.Id);
    }

    [SkippableFact]
    public async Task A_counter_naming_a_line_that_is_not_on_the_PO_is_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var foreign = new PoNegotiationLineInput(Guid.NewGuid(), 9m, null, s.Setup.PriceUnit);

        var resp = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", Counter(foreign));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await resp.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(resp)).Errors.Should().ContainSingle().Which.Should().Contain("does not belong to this PO");
        await AssertStillOpenAsync(s, first.Id);
    }

    [SkippableFact]
    public async Task A_counter_keeps_the_lines_it_omits_carries_the_price_and_drops_a_line_it_reverts_to_the_PO()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var lineB = await AddLineAsync(s.PoId, positionNo: 20, qty: 20m, priceUnit: 50m);
        PoNegotiationLineInput B(decimal qty) => new(lineB, qty, null, 50m);

        // Round 1: the supplier moves both lines (A 10 → 8, B 20 → 15).
        var first = await PostOkAsync<PoNegotiationDto>(s.Supplier, NegotiationsUrl,
            new CreatePoNegotiationRequest(s.PoId, "both lines", new() { s.Line(8m), B(15m) }));

        // Round 2: the buyer names ONLY line A (and sends a price, which must be ignored). B keeps round 1's 15.
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id,
            new PoNegotiationLineInput(s.Setup.PoLineId, 9m, null, 999m));
        second.Lines.Select(l => (l.PositionNo, l.OriginalQty, l.NegotiatedQty, l.OriginalPrice, l.NegotiatedPrice)).Should().Equal(
            (10, 10m, 9m, 100m, 100m),
            (20, 20m, 15m, 50m, 50m));

        // Round 3: the supplier sends A back to the PO's own 10. A then equals the live line, so it is no longer stored — the
        // round carries B alone — yet A's effective value is the PO's.
        var third = await CounterAsync(s.Supplier, SupplierCounter, second.Id, s.Line(10m));
        third.Lines.Select(l => (l.PositionNo, l.NegotiatedQty)).Should().Equal((20, 15m));

        var detail = await GetAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{third.Id}");
        var a = detail.Lines.Single(l => l.PositionNo == 10);
        a.Should().BeEquivalentTo(new { IsChanged = false, OriginalQty = 10m, NegotiatedQty = 10m, PreviousRoundQty = 9m });
        var b = detail.Lines.Single(l => l.PositionNo == 20);
        b.Should().BeEquivalentTo(new { IsChanged = true, OriginalQty = 20m, NegotiatedQty = 15m, PreviousRoundQty = 15m });
    }

    [SkippableFact]
    public async Task A_counter_delivery_before_the_PO_date_is_rejected_only_when_the_date_changed_against_the_round_it_answers()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();

        // ERP data can carry a line delivery that predates the PO (a same-day date arrives at midnight, the PO date with a time).
        var stale = s.PoDate.AddHours(-2);
        await SetLineAsync(s.Setup.PoLineId, deliveryDate: stale);

        // Round 1 moves only the quantity; the editor re-sends the untouched (stale) delivery verbatim and it is not faulted.
        var first = await OpenAsync(s, qty: 8m, delivery: stale);

        // A counter that CHANGES the delivery to a moment still before the PO date is rejected...
        var earlier = await s.Buyer.PostAsJsonAsync(
            $"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", Counter(s.Line(8m, s.PoDate.AddHours(-1))));
        earlier.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await earlier.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(earlier)).Errors.Should().ContainSingle().Which.Should().Contain("cannot be earlier than the PO date");
        await AssertStillOpenAsync(s, first.Id);

        // ...while a quantity-only counter that re-sends the round's stale delivery is accepted: the buyer never proposed that date.
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m, stale));
        second.Lines.Should().ContainSingle().Which.NegotiatedDeliveryDate.Should().Be(stale);
    }

    [SkippableFact]
    public async Task A_counter_delivery_exactly_at_the_PO_instant_is_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: s.Setup.OrderQty, delivery: s.PoDate.AddDays(3));

        // The floor is delivery >= PO date, so the boundary itself is legal.
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(s.Setup.OrderQty, s.PoDate));

        second.Lines.Should().ContainSingle().Which.NegotiatedDeliveryDate.Should().Be(s.PoDate);
    }

    [SkippableFact]
    public async Task A_counter_judges_the_quantity_scale_only_when_the_quantity_changed_against_the_round_it_answers()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();

        // A unit that is not in the unit master allows 3 decimal places. ERP-fed data need not fit it: the order quantity
        // already carries four.
        var unit = $"NOUNIT-{s.Setup.Tag}";
        await SetLineAsync(s.Setup.PoLineId, unit: unit, orderQty: 10.1234m);

        // Round 1 moves only the delivery; the quantity is re-sent verbatim and, not being a proposal, not judged.
        var first = await OpenAsync(s, qty: 10.1234m, delivery: s.PoDate.AddDays(2));

        // A counter that CHANGES the quantity to one with too many decimals is rejected...
        var tooMany = await s.Buyer.PostAsJsonAsync($"{NegotiationsUrl}/{first.Id}/{BuyerCounter}", Counter(s.Line(7.4567m, s.PoDate.AddDays(2))));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await tooMany.Content.ReadAsStringAsync());
        (await Read<PoNegotiationDto>(tooMany)).Errors.Should().Equal(
            $"Quantity on line {s.Setup.PoPositionNo}/1 for unit {unit} allows at most 3 decimal places.");

        // ...while one that moves only the date and re-sends the round's own quantity is accepted.
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(10.1234m, s.PoDate.AddDays(4)));
        second.Lines.Should().ContainSingle().Which.NegotiatedQty.Should().Be(10.1234m);
    }

    // ───────────────────────────── what is visible ─────────────────────────────

    [SkippableFact]
    public async Task The_supplier_reads_the_buyers_round_with_the_whole_chain_and_the_head_pointing_at_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));

        // The buyer's round carries the SUPPLIER's seccode owner, so RLS shows it to the supplier like its own.
        (await RoundAsync(second.Id)).Should().BeEquivalentTo(new
        {
            SeccodeId = s.Setup.Supplier.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
        });

        var detail = await GetAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{second.Id}");

        detail.Should().BeEquivalentTo(new
        {
            Id = second.Id, RoundNo = 2, ProposedBy = "Buyer", ParentNegotiationId = first.Id, HeadNegotiationId = second.Id,
        });
        detail.Rounds.Should().NotBeNull();
        detail.Rounds!.Count.Should().Be(2);
        detail.Rounds.Select(r => (r.Id, r.RoundNo, r.ProposedBy, r.NegotiationStatus)).Should().Equal(
            (first.Id, 1, "Supplier", "Countered"),
            (second.Id, 2, "Buyer", "Submitted"));
        detail.Rounds.Select(r => r.SubmittedBy).Should().Equal(SecurityTestHarness.Users.Supplier, SecurityTestHarness.Users.Buyer);
        detail.Rounds[0].ReviewedBy.Should().Be(SecurityTestHarness.Users.Buyer, because: "the buyer is who countered round 1");

        // The line diff: this round's whole proposal (10 → 9), and what the PARENT round had proposed (8).
        var line = detail.Lines.Should().ContainSingle().Subject;
        line.Should().BeEquivalentTo(new { IsChanged = true, OriginalQty = 10m, NegotiatedQty = 9m, PreviousRoundQty = 8m });

        // The superseded round is still readable and points at the head; having no parent it has no previous-round values.
        var old = await GetAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{first.Id}");
        old.Should().BeEquivalentTo(new { RoundNo = 1, HeadNegotiationId = second.Id });
        old.ParentNegotiationId.Should().BeNull();
        old.Rounds!.Select(r => r.Id).Should().Equal(first.Id, second.Id);
        old.Lines.Single().PreviousRoundQty.Should().BeNull();

        // A reviewer reads the same chain.
        var asBuyer = await GetAsync<PoNegotiationDto>(s.Buyer, $"{NegotiationsUrl}/{second.Id}");
        asBuyer.Rounds!.Select(r => r.Id).Should().Equal(first.Id, second.Id);
        asBuyer.HeadNegotiationId.Should().Be(second.Id);
    }

    [SkippableFact]
    public async Task The_list_leaves_out_Countered_rounds_unless_that_status_is_asked_for()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));

        foreach (var (who, client) in new[] { ("supplier", s.Supplier), ("buyer", s.Buyer) })
        {
            var open = await GetAsync<List<PoNegotiationListItemDto>>(client, NegotiationsUrl);
            open.Select(r => r.Id).Should().Contain(second.Id, because: who).And.NotContain(first.Id, because: who);
            var row = open.Single(r => r.Id == second.Id);
            (row.RoundNo, row.ProposedBy, row.NegotiationStatus).Should().Be((2, "Buyer", "Submitted"));

            var submitted = await GetAsync<List<PoNegotiationListItemDto>>(client, $"{NegotiationsUrl}?status=Submitted");
            submitted.Select(r => r.Id).Should().Contain(second.Id, because: who).And.NotContain(first.Id, because: who);

            var countered = await GetAsync<List<PoNegotiationListItemDto>>(client, $"{NegotiationsUrl}?status=Countered");
            countered.Select(r => r.Id).Should().Contain(first.Id, because: who).And.NotContain(second.Id, because: who);
            var old = countered.Single(r => r.Id == first.Id);
            (old.RoundNo, old.ProposedBy).Should().Be((1, "Supplier"));
        }
    }

    [SkippableFact]
    public async Task A_counter_writes_history_rows_labelled_by_round_while_round_one_keeps_its_original_labels()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SceneAsync();
        var first = await OpenAsync(s, qty: 8m);
        var second = await CounterAsync(s.Buyer, BuyerCounter, first.Id, s.Line(9m));
        await PostOkAsync<PoNegotiationDto>(s.Supplier, $"{NegotiationsUrl}/{second.Id}/accept");

        var line = $"Line {s.Setup.PoPositionNo}/1 {s.Setup.ItemCode}";
        var rows = await NegotiationHistoryAsync(s.PoId);

        // Round 1 is labelled exactly as before R18; round 2 names itself and its author; each answered round gets an outcome row.
        rows.Select(r => r.FieldName).Should().BeEquivalentTo(new[]
        {
            $"Negotiation · {line} · Qty",
            "Negotiation countered",
            $"Negotiation R2 (Buyer) · {line} · Qty",
            "Negotiation R2 (Buyer) accepted",
        });
        var one = rows.Single(r => r.FieldName == $"Negotiation · {line} · Qty");
        (one.OldValue, one.NewValue, one.ChangedBy).Should().Be(("10", "8", SecurityTestHarness.Users.Supplier));
        var two = rows.Single(r => r.FieldName == $"Negotiation R2 (Buyer) · {line} · Qty");
        (two.OldValue, two.NewValue, two.ChangedBy).Should().Be(("10", "9", SecurityTestHarness.Users.Buyer));
        rows.Single(r => r.FieldName == "Negotiation countered").NewValue.Should().Contain("round 2");
    }

    // ───────────────────────────── the SLA nudge ─────────────────────────────

    [SkippableFact]
    public async Task The_SLA_nudge_chases_a_supplier_round_awaiting_the_buyer_but_never_a_round_the_buyer_proposed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var slaWindow = TimeSpan.FromHours(48);
        var overdue = DateTime.UtcNow - slaWindow - TimeSpan.FromHours(1);
        var supplier = await _fx.CreateSupplierAsync(tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

        Guid supplierRound, buyerRound;
        string supplierPo, buyerPo;
        await using (var arrange = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = arrange.ServiceProvider.GetRequiredService<AppDbContext>();

            // PO A: the supplier's round has waited past the SLA window for the buyer.
            var poA = SeedPo(db, supplier, $"PO-RND-SLA-A-{tag}");
            supplierRound = SeedRound(db, poA, supplier, 1, NegotiationParty.Supplier, PoNegotiationStatus.Submitted, null, overdue).Id;
            supplierPo = poA.PoNumber;

            // PO B: the supplier's round 1 was countered by the buyer, whose round 2 has waited past the window for the SUPPLIER.
            var poB = SeedPo(db, supplier, $"PO-RND-SLA-B-{tag}");
            var countered = SeedRound(db, poB, supplier, 1, NegotiationParty.Supplier, PoNegotiationStatus.Countered, null, overdue);
            buyerRound = SeedRound(db, poB, supplier, 2, NegotiationParty.Buyer, PoNegotiationStatus.Submitted, countered.Id, overdue).Id;
            buyerPo = poB.PoNumber;

            await db.SaveChangesAsync();
        }

        await using (var pass = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = pass.ServiceProvider.GetRequiredService<IAppDbContext>();
            var renderer = pass.ServiceProvider.GetRequiredService<IEmailTemplateRenderer>();
            await SlaNudgeWorker.NudgeOnceAsync(db, renderer, slaWindow, batchSize: 50, NullLogger.Instance, CancellationToken.None);
        }

        await using var check = _fx.Factory.Services.CreateAsyncScope();
        var verify = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Whichever pass handled it (this one, or the host's own worker), the supplier's round has been nudged exactly once...
        (await verify.PurchaseOrderNegotiations.IgnoreQueryFilters().SingleAsync(n => n.Id == supplierRound))
            .NudgeSentAt.Should().NotBeNull(because: "the buyer owes the answer to a supplier round");
        (await NudgeMailCountAsync(verify, supplierPo)).Should().Be(1);

        // ...and the buyer's round — which waits on the supplier — has not: no stamp, no e-mail to the buyer.
        (await verify.PurchaseOrderNegotiations.IgnoreQueryFilters().SingleAsync(n => n.Id == buyerRound))
            .NudgeSentAt.Should().BeNull(because: "chasing the buyer about a round it proposed would chase the wrong party");
        (await NudgeMailCountAsync(verify, buyerPo)).Should().Be(0);
    }

    // ───────────────────────────── arrange ─────────────────────────────

    /// <summary>A fresh Released PO (one line: qty 10 @ 100, no delivery date) with a supplier user, and a buyer mapped to its supplier.</summary>
    private sealed record Scene(ProcureToPayFlow.Setup Setup, HttpClient Supplier, HttpClient Buyer, DateTime PoDate)
    {
        public Guid PoId => Setup.PoId;

        /// <summary>The seeded line as a request line: <paramref name="qty"/> and <paramref name="delivery"/> proposed, the PO's own price echoed.</summary>
        public PoNegotiationLineInput Line(decimal qty, DateTime? delivery = null) => new(Setup.PoLineId, qty, delivery, Setup.PriceUnit);
    }

    private async Task<Scene> SceneAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);

        // The buyer's RLS-scoped reads and writes reach the supplier-owned rows through a SupplierUserMap (the same set-up the ASN
        // approval suites use).
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);

        // The mapping is all the buyer needs; the PO itself must not name the buyer. Several tests end with a Supplier round still
        // open, and the test DB persists: with a resolvable BuyerUserId such a round is, 48h on, selected AND counted by the SLA nudge,
        // which breaks SlaNudgeWorkerTests (enqueued == 1). Buyer-less leftovers are only stamped, never counted — as the older
        // PoNegotiation* tests' leftovers have always been.
        await using (var detach = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = detach.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == setup.PoId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.BuyerUserId, (Guid?)null));
        }

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        return new Scene(setup, supplier, buyer, await PoDateAsync(setup.PoId));
    }

    /// <summary>Round 1: the supplier proposes <paramref name="qty"/> (and optionally a delivery) on the seeded line.</summary>
    private async Task<PoNegotiationDto> OpenAsync(Scene s, decimal qty, DateTime? delivery = null)
        => await PostOkAsync<PoNegotiationDto>(
            s.Supplier, NegotiationsUrl, new CreatePoNegotiationRequest(s.PoId, "opening proposal", new() { s.Line(qty, delivery) }));

    /// <summary>A counter through the real endpoint (<c>counter</c> for the buyer, <c>supplier-counter</c> for the supplier); returns the NEW round.</summary>
    private async Task<PoNegotiationDto> CounterAsync(HttpClient client, string action, Guid roundId, params PoNegotiationLineInput[] lines)
        => await PostOkAsync<PoNegotiationDto>(client, $"{NegotiationsUrl}/{roundId}/{action}", Counter(lines));

    private static CounterPoNegotiationRequest Counter(params PoNegotiationLineInput[] lines) => new("counter-offer", lines.ToList());

    /// <summary>A second line on the PO (no delivery date, unit EA); returns its id.</summary>
    private async Task<Guid> AddLineAsync(Guid poId, int positionNo, decimal qty, decimal priceUnit)
    {
        var id = Guid.NewGuid();
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PurchaseOrderLines.Add(new PurchaseOrderLine
        {
            Id = id, PurchaseOrderId = poId, PositionNo = positionNo, SequenceNo = 1,
            ItemCode = $"RND-{id.ToString("N")[..12]}", OrderUnit = "EA", OrderQty = qty,
            PriceUnit = priceUnit, Price = priceUnit * qty, DeliveryDate = null,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Rewrites the seeded PO line directly (ERP stays the line master, so the portal has no API for this).</summary>
    private async Task SetLineAsync(Guid lineId, DateTime? deliveryDate = null, string? unit = null, decimal? orderQty = null)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == lineId);
        if (deliveryDate is { } date) await lines.ExecuteUpdateAsync(u => u.SetProperty(l => l.DeliveryDate, date));
        if (unit is not null) await lines.ExecuteUpdateAsync(u => u.SetProperty(l => l.OrderUnit, unit));
        if (orderQty is { } qty) await lines.ExecuteUpdateAsync(u => u.SetProperty(l => l.OrderQty, qty));
    }

    private async Task SetAllowNegotiateAsync(Guid supplierId, bool allow)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Suppliers.IgnoreQueryFilters().Where(x => x.Id == supplierId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.AllowNegotiate, allow));
    }

    private static PurchaseOrder SeedPo(AppDbContext db, SecurityTestHarness.SeededSupplier supplier, string poNumber)
    {
        var now = DateTime.UtcNow;
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(), PoNumber = poNumber, SupplierId = supplier.SupplierId, PoType = PoType.Material,
            PoDate = now.Date, PoStatus = PoStatus.Negotiation, BuyerUserId = SecurityTestHarness.BuyerUserId,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        };
        db.PurchaseOrders.Add(po);
        return po;
    }

    private static PurchaseOrderNegotiation SeedRound(
        AppDbContext db, PurchaseOrder po, SecurityTestHarness.SeededSupplier supplier, int roundNo, NegotiationParty proposedBy,
        PoNegotiationStatus status, Guid? parent, DateTime submittedAt)
    {
        var round = new PurchaseOrderNegotiation
        {
            Id = Guid.NewGuid(), PurchaseOrderId = po.Id, PoNumber = po.PoNumber, SupplierId = supplier.SupplierId,
            NegotiationStatus = status, ParentNegotiationId = parent, RoundNo = roundNo, ProposedBy = proposedBy,
            PreviousPoStatus = PoStatus.Released, SubmittedAt = submittedAt,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = submittedAt,
        };
        db.PurchaseOrderNegotiations.Add(round);
        return round;
    }

    // ───────────────────────────── act / read ─────────────────────────────

    private async Task<T> PostOkAsync<T>(HttpClient client, string url, object? body = null)
    {
        var resp = body is null ? await client.PostAsync(url, null) : await client.PostAsJsonAsync(url, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        return (await Read<T>(resp)).Data!;
    }

    private async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        return (await Read<T>(resp)).Data!;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<Result<T>>(await resp.Content.ReadAsStringAsync(), Json)!;

    // ───────────────────────────── assert / database ─────────────────────────────

    /// <summary>A rejected action wrote nothing: round 1 is still the PO's only round, still open, and the PO is still in Negotiation.</summary>
    private async Task AssertStillOpenAsync(Scene s, Guid roundId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.PurchaseOrderNegotiations.IgnoreQueryFilters().CountAsync(n => n.PurchaseOrderId == s.PoId))
            .Should().Be(1, because: "a refused counter opens no round");
        (await RoundAsync(roundId)).NegotiationStatus.Should().Be(PoNegotiationStatus.Submitted);
        (await PoStatusAsync(s.PoId)).Should().Be(PoStatus.Negotiation);
    }

    private async Task<PurchaseOrderNegotiation> RoundAsync(Guid id)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderNegotiations.IgnoreQueryFilters().Include(n => n.Lines).SingleAsync(n => n.Id == id);
    }

    private async Task<PoStatus> PoStatusAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId).Select(p => p.PoStatus).SingleAsync();
    }

    private async Task<DateTime> PoDateAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId).Select(p => p.PoDate).SingleAsync();
    }

    /// <summary>
    /// The ERP-push outbox rows queued for one round. Existence is what is asserted — the dispatcher worker runs in the test host and
    /// advances a row's status (and may have finished with it) at any moment.
    /// </summary>
    private async Task<List<OutboxMessage>> PushesForAsync(Guid roundId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.TransactionType == OutboxTransactionType.PoNegotiationApprove && m.EntityId == roundId)
            .ToListAsync();
    }

    /// <summary>The deterministic outbox key an approval of <paramref name="roundId"/> queues: tenant + PO number + round id.</summary>
    private static string PushKey(Scene s, Guid roundId)
        => OutboxKey.For(OutboxEntity.PurchaseOrder, IntegrationTestFixture.TenantId, $"{s.Setup.PoNumber}|{roundId:N}", "negotiation-approve");

    /// <summary>The PO's negotiation rows on its History tab (PO-targeted audit rows whose label starts "Negotiation").</summary>
    private async Task<List<AuditEntry>> NegotiationHistoryAsync(Guid poId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditEntries.IgnoreQueryFilters()
            .Where(a => a.EntityName == nameof(PurchaseOrder) && a.EntityId == poId && a.FieldName.StartsWith("Negotiation"))
            .ToListAsync();
    }

    private static Task<int> NudgeMailCountAsync(AppDbContext db, string poNumber)
        => db.EmailOutbox.IgnoreQueryFilters()
            .CountAsync(m => m.TemplateKey == "PoNegotiationNudge" && m.Subject.Contains(poNumber));
}
