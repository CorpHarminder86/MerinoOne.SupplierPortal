using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R4 — TSD R4 Addendum §7.2 / UC-ASN-05 (cases A–D). Pure, DB-less tests for the two-tier over-ship tolerance
/// resolver. The resolver is <c>SupplierItem?.OverShipTolerancePct ?? Item.OverShipTolerancePct</c>: a present-but-
/// NULL supplier override INHERITS the Item-master floor (B), a missing supplier row also inherits (C), a non-null
/// override wins (A), and an explicit 0 caps at "no over-ship" (D).
/// </summary>
public class OverShipToleranceTests
{
    private static Item Item(decimal pct) => new() { OverShipTolerancePct = pct };
    private static SupplierItem Si(decimal? pct) => new() { OverShipTolerancePct = pct };

    [Fact]
    public void CaseA_supplierItem_nonNull_overrides_item()
    {
        // Item 10%, SupplierItem 2% (non-null) → resolved 2%.
        OverShipTolerance.ResolveOverShipTolerance(Si(2m), Item(10m)).Should().Be(2m);
    }

    [Fact]
    public void CaseB_supplierItem_null_inherits_item()
    {
        // Item 10%, SupplierItem row exists but tolerance NULL → resolved 10% (inherit).
        OverShipTolerance.ResolveOverShipTolerance(Si(null), Item(10m)).Should().Be(10m);
    }

    [Fact]
    public void CaseC_noSupplierItemRow_inherits_item()
    {
        // Item 10%, no SupplierItem row → resolved 10%.
        OverShipTolerance.ResolveOverShipTolerance(null, Item(10m)).Should().Be(10m);
    }

    [Fact]
    public void CaseD_supplierItem_explicitZero_caps_at_zero()
    {
        // Item 10%, SupplierItem 0% (explicit) → resolved 0% (no over-ship).
        OverShipTolerance.ResolveOverShipTolerance(Si(0m), Item(10m)).Should().Be(0m);
    }

    [Theory]
    [InlineData(0, 1.00)]
    [InlineData(5, 1.05)]
    [InlineData(10, 1.10)]
    [InlineData(2.5, 1.025)]
    public void Factor_is_one_plus_pct_over_hundred(decimal pct, decimal expected)
    {
        OverShipTolerance.Factor(pct).Should().Be(expected);
    }
}
