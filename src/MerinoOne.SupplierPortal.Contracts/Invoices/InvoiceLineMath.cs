namespace MerinoOne.SupplierPortal.Contracts.Invoices;

/// <summary>
/// R19 (2026-11-16) — the ONE place an invoice line's discount / taxable / tax are computed, so the generator, the
/// ASN-to-invoice builder and the PDF all agree. The PO line's <c>DiscountAmount</c> is PER UNIT (verified against real
/// LN data: <c>OrderQty * PriceUnit - Price == OrderQty * DiscountAmount</c>), so the line discount is the per-unit
/// value times the billed quantity. All rounding is 2 decimal places, away from zero.
/// </summary>
public static class InvoiceLineMath
{
    private static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The per-unit discount of a PO line. LN sends the discount either as an absolute per-unit <paramref name="discountAmount"/>
    /// or ONLY as a <paramref name="discountPct"/> (amount 0). Amount wins when positive; else pct of the unit price; else 0.
    /// Not rounded here — rounding happens once in <see cref="LineDiscount"/> on the line total.
    /// Examples: (0, 10, 70) =&gt; 7 (30 x 70, 10% =&gt; discount 210, taxable 1890); (0, 5, 8) =&gt; 0.4 (1000 x 8 =&gt; 400, taxable 7600);
    /// (10, 0, 250) =&gt; 10 (66 x 250 =&gt; 660, taxable 15840); both set =&gt; the amount.
    /// </summary>
    public static decimal PerUnitDiscount(decimal? discountAmount, decimal? discountPct, decimal unitPrice)
    {
        if (discountAmount is > 0m) return discountAmount.Value;
        if (discountPct is > 0m) return unitPrice * discountPct.Value / 100m;
        return 0m;
    }

    /// <summary>round(perUnitDiscount * billedQty, 2); 0 when billedQty is not positive or the discount is null / not positive.</summary>
    public static decimal LineDiscount(decimal? perUnitDiscount, decimal billedQty)
    {
        if (billedQty <= 0 || perUnitDiscount is null || perUnitDiscount.Value <= 0) return 0m;
        return Round2(perUnitDiscount.Value * billedQty);
    }

    /// <summary>max(0, round(billedQty * unitPrice, 2) - lineDiscount).</summary>
    public static decimal Taxable(decimal billedQty, decimal unitPrice, decimal lineDiscount)
        => Math.Max(0m, Round2(billedQty * unitPrice) - lineDiscount);

    /// <summary>round(taxable * ratePct / 100, 2); 0 when the rate is null.</summary>
    public static decimal Tax(decimal taxable, decimal? ratePct)
        => ratePct is null ? 0m : Round2(taxable * ratePct.Value / 100m);
}
