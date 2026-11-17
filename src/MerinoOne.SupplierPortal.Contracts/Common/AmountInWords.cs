using System.Globalization;
using System.Text;

namespace MerinoOne.SupplierPortal.Contracts.Common;

/// <summary>
/// R19 (2026-11-16) — spells an invoice amount out in words for the invoice PDF. INR (or a null / blank currency) uses the
/// Indian numbering system (Thousand / Lakh / Crore) and reads "Indian Rupees ... and Fifty Paise Only".
/// <para>2026-11-17 feedback — every other currency is written in full words too: the currency name spelled out, the
/// international numbering system (Thousand / Million / Billion) and the minor unit in words — "Euro Eight Hundred Forty
/// Eight and Forty Five Cents Only". A currency missing from <see cref="Currencies"/> falls back to its code and an
/// "and 45/100" fraction, because its minor-unit name is not known.</para>
/// </summary>
public static class AmountInWords
{
    private static readonly string[] Small =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
        "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety",
    };

    /// <summary>Currency name + minor unit (singular, plural) for the non-INR currencies the portal trades in.</summary>
    private static readonly Dictionary<string, (string Name, string MinorOne, string MinorMany)> Currencies =
        new(StringComparer.Ordinal)
        {
            ["EUR"] = ("Euro", "Cent", "Cents"),
            ["USD"] = ("US Dollars", "Cent", "Cents"),
            ["GBP"] = ("Pound Sterling", "Penny", "Pence"),
            ["AUD"] = ("Australian Dollars", "Cent", "Cents"),
            ["CAD"] = ("Canadian Dollars", "Cent", "Cents"),
            ["SGD"] = ("Singapore Dollars", "Cent", "Cents"),
            ["HKD"] = ("Hong Kong Dollars", "Cent", "Cents"),
            ["NZD"] = ("New Zealand Dollars", "Cent", "Cents"),
            ["CHF"] = ("Swiss Francs", "Rappen", "Rappen"),
            ["AED"] = ("UAE Dirhams", "Fils", "Fils"),
            ["SAR"] = ("Saudi Riyals", "Halala", "Halalas"),
            ["CNY"] = ("Chinese Yuan", "Fen", "Fen"),
            ["JPY"] = ("Japanese Yen", "Sen", "Sen"),
        };

    public static string Format(decimal amount, string? currencyCode)
    {
        var code = string.IsNullOrWhiteSpace(currencyCode) ? "INR" : currencyCode.Trim().ToUpperInvariant();
        var isInr = code == "INR";
        var known = Currencies.TryGetValue(code, out var cur);

        var abs = Math.Abs(amount);
        var whole = decimal.Truncate(abs);
        var fraction = (int)decimal.Round((abs - whole) * 100m, 0, MidpointRounding.AwayFromZero);
        if (fraction >= 100) { whole += 1m; fraction -= 100; }

        var negative = amount < 0 && (whole > 0 || fraction > 0);

        var sb = new StringBuilder();
        sb.Append(isInr ? "Indian Rupees" : known ? cur.Name : code).Append(' ');
        if (negative) sb.Append("Minus ");
        sb.Append(isInr ? Spell((long)whole) : SpellInternational((long)whole));
        if (fraction > 0)
        {
            if (isInr) sb.Append(" and ").Append(Spell(fraction)).Append(" Paise");
            else if (known) sb.Append(" and ").Append(Below1000(fraction)).Append(' ').Append(fraction == 1 ? cur.MinorOne : cur.MinorMany);
            else sb.Append(" and ").Append(fraction.ToString("00", CultureInfo.InvariantCulture)).Append("/100");
        }
        sb.Append(" Only");
        return sb.ToString();
    }

    // International grouping: billion / million / thousand / remainder; the billion part may exceed 999, so it recurses.
    private static string SpellInternational(long n)
    {
        if (n == 0) return "Zero";

        var parts = new List<string>();
        var billion = n / 1_000_000_000;
        n %= 1_000_000_000;
        var million = n / 1_000_000;
        n %= 1_000_000;
        var thousand = n / 1_000;
        n %= 1_000;

        if (billion > 0) parts.Add(SpellInternational(billion) + " Billion");
        if (million > 0) parts.Add(Below1000(million) + " Million");
        if (thousand > 0) parts.Add(Below1000(thousand) + " Thousand");
        if (n > 0) parts.Add(Below1000(n));
        return string.Join(' ', parts);
    }

    // Indian grouping: crore (1,00,00,000) / lakh (1,00,000) / thousand / remainder. The crore part may itself exceed
    // 99, so it recurses ("One Hundred Crore").
    private static string Spell(long n)
    {
        if (n == 0) return "Zero";

        var parts = new List<string>();
        var crore = n / 10_000_000;
        n %= 10_000_000;
        var lakh = n / 100_000;
        n %= 100_000;
        var thousand = n / 1_000;
        n %= 1_000;

        if (crore > 0) parts.Add(Spell(crore) + " Crore");
        if (lakh > 0) parts.Add(Below1000(lakh) + " Lakh");
        if (thousand > 0) parts.Add(Below1000(thousand) + " Thousand");
        if (n > 0) parts.Add(Below1000(n));
        return string.Join(' ', parts);
    }

    // 1..999, no "and" between hundreds and tens ("Eight Hundred Forty").
    private static string Below1000(long n)
    {
        var parts = new List<string>();
        var hundreds = (int)(n / 100);
        var rest = (int)(n % 100);
        if (hundreds > 0) parts.Add(Small[hundreds] + " Hundred");
        if (rest > 0)
        {
            if (rest < 20) parts.Add(Small[rest]);
            else parts.Add(rest % 10 == 0 ? Tens[rest / 10] : Tens[rest / 10] + " " + Small[rest % 10]);
        }
        return string.Join(' ', parts);
    }
}
