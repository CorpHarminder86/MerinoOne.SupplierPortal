using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R4 — TSD R4 Addendum §6.3 / §6.4, Component 2 (PO-Change Sync). Pure, DB-less tests for the material-change
/// diff that decides whether an inbound PO re-push (ERP Modify) re-arms the confirmation gate (UC-PO-06) or is
/// non-material and only bumps version (UC-PO-07). Material = order qty / price / delivery date / line add-remove;
/// non-material = notes / item description / tax / discount.
/// </summary>
public class PoMaterialChangeTests
{
    private static PurchaseOrderLine Existing(int pos, int seq, decimal qty, decimal priceUnit = 1m, decimal price = 100m, DateTime? delivery = null)
        => new() { PositionNo = pos, SequenceNo = seq, OrderQty = qty, PriceUnit = priceUnit, Price = price, DeliveryDate = delivery };

    private static PoLineRecord Incoming(int pos, int seq, decimal qty, decimal priceUnit = 1m, decimal price = 100m,
        DateTime? delivery = null, string? notes = null, string? itemDesc = null, decimal discountAmount = 0m)
        => new(PositionNo: pos, SequenceNo: seq, ItemCode: "ITM", ItemDescription: itemDesc,
               OrderUnit: "EA", OrderQty: qty, PriceUnit: priceUnit, Price: price, DiscountAmount: discountAmount,
               DeliveryDate: delivery);

    // ── Material: order qty changed (UC-PO-06). ─────────────────────────────────────────────────────
    [Fact]
    public void OrderQty_change_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100) };
        var after = new[] { Incoming(10, 1, qty: 120) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Material: unit price changed. ───────────────────────────────────────────────────────────────
    [Fact]
    public void UnitPrice_change_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100, priceUnit: 1m) };
        var after = new[] { Incoming(10, 1, qty: 100, priceUnit: 2m) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Material: extended price changed. ───────────────────────────────────────────────────────────
    [Fact]
    public void ExtendedPrice_change_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100, price: 100m) };
        var after = new[] { Incoming(10, 1, qty: 100, price: 250m) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Material: delivery date changed. ────────────────────────────────────────────────────────────
    [Fact]
    public void DeliveryDate_change_is_material()
    {
        var d1 = new DateTime(2026, 7, 1);
        var d2 = new DateTime(2026, 7, 15);
        var before = new[] { Existing(10, 1, qty: 100, delivery: d1) };
        var after = new[] { Incoming(10, 1, qty: 100, delivery: d2) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Material: a line was ADDED (by natural key). ────────────────────────────────────────────────
    [Fact]
    public void Line_added_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100) };
        var after = new[] { Incoming(10, 1, qty: 100), Incoming(20, 1, qty: 50) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Material: a line was REMOVED (persisted key absent from the push). ──────────────────────────
    [Fact]
    public void Line_removed_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100), Existing(20, 1, qty: 50) };
        var after = new[] { Incoming(10, 1, qty: 100) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── Same position, different sequence is a DIFFERENT line → add+remove → material. ──────────────
    [Fact]
    public void Same_position_different_sequence_is_material()
    {
        var before = new[] { Existing(10, 1, qty: 100) };
        var after = new[] { Incoming(10, 2, qty: 100) };
        PoMaterialChange.IsMaterial(before, after).Should().BeTrue();
    }

    // ── NON-material: only notes / item description / discount changed (UC-PO-07). ──────────────────
    [Fact]
    public void Notes_and_description_only_is_not_material()
    {
        var before = new[] { Existing(10, 1, qty: 100, priceUnit: 1m, price: 100m) };
        var after = new[] { Incoming(10, 1, qty: 100, priceUnit: 1m, price: 100m, itemDesc: "renamed", discountAmount: 5m) };
        PoMaterialChange.IsMaterial(before, after).Should().BeFalse();
    }

    // ── NON-material: identical lines. ──────────────────────────────────────────────────────────────
    [Fact]
    public void Identical_lines_is_not_material()
    {
        var d = new DateTime(2026, 7, 1);
        var before = new[] { Existing(10, 1, qty: 100, priceUnit: 2m, price: 200m, delivery: d), Existing(20, 1, qty: 50) };
        var after = new[] { Incoming(10, 1, qty: 100, priceUnit: 2m, price: 200m, delivery: d), Incoming(20, 1, qty: 50) };
        PoMaterialChange.IsMaterial(before, after).Should().BeFalse();
    }

    // ── DescribeDiff renders the qty delta + revised remaining-to-ship balance (§14). ───────────────
    [Fact]
    public void DescribeDiff_renders_qty_delta_and_remaining_balance()
    {
        // Line 10 qty 100→120 with 60 already shipped → remaining = 120 − 60 = 60.
        var before = new[] { new PoLineChangeSnapshot(10, 1, OrderQty: 100m, PriceUnit: 1m, Price: 100m, DeliveryDate: null, ShippedQtyToDate: 60m) };
        var after = new[] { Incoming(10, 1, qty: 120m) };
        var diff = PoMaterialChange.DescribeDiff(before, after);
        diff.Should().Contain("Line 10");
        diff.Should().Contain("100→120");
        diff.Should().Contain("60 remaining");
    }

    [Fact]
    public void DescribeDiff_reports_added_and_removed_lines()
    {
        var before = new[]
        {
            new PoLineChangeSnapshot(10, 1, 100m, 1m, 100m, null, 0m),
            new PoLineChangeSnapshot(30, 1, 10m, 1m, 10m, null, 0m),
        };
        var after = new[] { Incoming(10, 1, qty: 100m), Incoming(20, 1, qty: 50m) };
        var diff = PoMaterialChange.DescribeDiff(before, after);
        diff.Should().Contain("Line 20 added");
        diff.Should().Contain("Line 30 removed");
    }
}
