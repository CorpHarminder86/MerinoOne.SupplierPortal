using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;
using LineQty = MerinoOne.SupplierPortal.Application.Shipments.FulfilmentStatusDeriver.LineQty;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R5 — TSD R5 Addendum §11.2 (Component 7, fulfilment-derived statuses). Pure, DB-less tests for the
/// on-ASN-Submit PO fulfilment-status derivation: the three-owner milestone table. Exercises:
/// <list type="bullet">
///   <item>all lines fully shipped → FullyShipped (NOT Delivered — that is ERP-owned, §11.2 / UC-SM-08);</item>
///   <item>some but not all shipped → PartiallyDelivered;</item>
///   <item>nothing shipped → unchanged;</item>
///   <item>terminal / ERP-owned / pre-fulfilment states (Cancelled / Closed / Delivered / Draft / Rejected /
///         Negotiation / Approved) are never clobbered;</item>
///   <item>an already-FullyShipped PO is not regressed to PartiallyDelivered and re-deriving FullyShipped is a
///         no-op.</item>
/// </list>
/// </summary>
public class FulfilmentStatusDeriverTests
{
    private static LineQty[] Lines(params (decimal order, decimal shipped)[] xs)
        => xs.Select(x => new LineQty(x.order, x.shipped)).ToArray();

    // ── EVERY line fully shipped → FullyShipped (never Delivered). ──
    [Fact]
    public void All_lines_fully_shipped_derives_FullyShipped()
    {
        var lines = Lines((10m, 10m), (20m, 20m));
        var res = FulfilmentStatusDeriver.Derive(PoStatus.Accepted, lines);
        res.Should().Be(PoStatus.FullyShipped,
            because: "all lines shipped == ordered → FullyShipped (§11.2)");
    }

    [Fact]
    public void All_lines_fully_shipped_never_derives_Delivered()
    {
        // §11.2 / UC-SM-08 — full shipment alone NEVER sets Delivered; only the mapped ERP status advances it.
        var lines = Lines((5m, 5m));
        var res = FulfilmentStatusDeriver.Derive(PoStatus.PartiallyDelivered, lines);
        res.Should().Be(PoStatus.FullyShipped);
        res.Should().NotBe(PoStatus.Delivered, because: "Delivered is ERP-owned, never quantity-derived (UC-SM-08)");
    }

    [Fact]
    public void Over_shipped_line_still_counts_as_fully_shipped()
    {
        // ShippedQtyToDate >= OrderQty (over-ship tolerance consumed) is still "fully shipped".
        var lines = Lines((10m, 11m), (20m, 20m));
        FulfilmentStatusDeriver.Derive(PoStatus.Accepted, lines).Should().Be(PoStatus.FullyShipped);
    }

    // ── SOME but not all shipped → PartiallyDelivered. ──
    [Fact]
    public void Partial_shipment_derives_PartiallyDelivered()
    {
        var lines = Lines((10m, 4m), (20m, 0m));
        FulfilmentStatusDeriver.Derive(PoStatus.Accepted, lines).Should().Be(PoStatus.PartiallyDelivered);
    }

    [Fact]
    public void One_line_full_one_line_partial_derives_PartiallyDelivered()
    {
        var lines = Lines((10m, 10m), (20m, 5m));
        FulfilmentStatusDeriver.Derive(PoStatus.Acknowledged, lines).Should().Be(PoStatus.PartiallyDelivered);
    }

    // ── NOTHING shipped → unchanged. ──
    [Fact]
    public void No_shipment_leaves_unchanged()
    {
        var lines = Lines((10m, 0m), (20m, 0m));
        FulfilmentStatusDeriver.Derive(PoStatus.Released, lines).Should().BeNull(
            because: "no line shipped → nothing to derive, leave PoStatus untouched");
    }

    [Fact]
    public void No_lines_leaves_unchanged()
    {
        FulfilmentStatusDeriver.Derive(PoStatus.Accepted, Array.Empty<LineQty>()).Should().BeNull();
    }

    // ── Derivation is valid from every in-fulfilment state (partial progress → PartiallyDelivered). ──
    // From an already-PartiallyDelivered PO the result COLLAPSES to null (target == current = no change); from the
    // other in-fulfilment entry states it newly derives PartiallyDelivered. (FullyShipped → partial would be a
    // regression and is exercised separately — the executor only ever ADDS qty so it is unreachable in practice.)
    [Theory]
    [InlineData(PoStatus.Accepted, PoStatus.PartiallyDelivered)]
    [InlineData(PoStatus.Acknowledged, PoStatus.PartiallyDelivered)]
    [InlineData(PoStatus.Released, PoStatus.PartiallyDelivered)]
    [InlineData(PoStatus.PartiallyDelivered, null)]   // already partial → no change (collapsed to null)
    [InlineData(PoStatus.FullyShipped, PoStatus.PartiallyDelivered)]
    public void In_fulfilment_states_derive_partial(PoStatus current, PoStatus? expected)
    {
        var lines = Lines((10m, 3m), (10m, 0m));
        FulfilmentStatusDeriver.Derive(current, lines).Should().Be(expected);
    }

    // ── TERMINAL / ERP-owned / pre-fulfilment states are NEVER clobbered. ──
    [Theory]
    [InlineData(PoStatus.Cancelled)]
    [InlineData(PoStatus.Closed)]
    [InlineData(PoStatus.Delivered)]
    [InlineData(PoStatus.Draft)]
    [InlineData(PoStatus.Rejected)]
    [InlineData(PoStatus.Negotiation)]
    [InlineData(PoStatus.Approved)]
    public void Terminal_and_non_fulfilment_states_are_not_clobbered(PoStatus current)
    {
        // Even with all-lines-fully-shipped quantities, a non-in-fulfilment status is left strictly untouched.
        var lines = Lines((10m, 10m), (20m, 20m));
        FulfilmentStatusDeriver.Derive(current, lines).Should().BeNull(
            because: $"{current} is terminal / ERP-owned / pre-fulfilment and must never be overwritten by quantity");
    }

    [Fact]
    public void Delivered_is_not_regressed_to_FullyShipped()
    {
        // A FullyShipped+Delivered PO (ERP advanced it) must not be pulled back to FullyShipped by a later derive.
        var lines = Lines((10m, 10m));
        FulfilmentStatusDeriver.Derive(PoStatus.Delivered, lines).Should().BeNull(
            because: "Delivered is ERP-owned; a quantity derive never regresses it (§11.2)");
    }

    // ── FullyShipped is not regressed, and re-deriving FullyShipped is a no-op (null). ──
    [Fact]
    public void FullyShipped_re_derive_is_a_noop()
    {
        var lines = Lines((10m, 10m), (20m, 20m));
        FulfilmentStatusDeriver.Derive(PoStatus.FullyShipped, lines).Should().BeNull(
            because: "re-deriving FullyShipped from FullyShipped is a no-op (collapsed to null)");
    }

    [Fact]
    public void PartiallyDelivered_re_derive_to_partial_is_a_noop()
    {
        var lines = Lines((10m, 4m), (20m, 0m));
        FulfilmentStatusDeriver.Derive(PoStatus.PartiallyDelivered, lines).Should().BeNull(
            because: "still partial → no status change (collapsed to null)");
    }

    [Fact]
    public void PartiallyDelivered_advances_to_FullyShipped_when_completed()
    {
        var lines = Lines((10m, 10m), (20m, 20m));
        FulfilmentStatusDeriver.Derive(PoStatus.PartiallyDelivered, lines).Should().Be(PoStatus.FullyShipped);
    }
}
