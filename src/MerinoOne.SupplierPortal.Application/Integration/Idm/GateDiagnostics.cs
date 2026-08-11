using System.Text;
using System.Text.RegularExpressions;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm;

/// <summary>
/// 2026-08-11 — turns a failed eligibility gate into a sentence a human can act on, for
/// <c>IdmDocumentOutbox.lastError</c>. Before this, a Blocked row carried NO diagnosis anywhere in the system:
/// the sync log showed "Blocked" with an empty error, and telling gate-fail apart from any other hold meant
/// hand-running the expression against a hand-built snapshot. Two sessions lost time to exactly that.
///
/// The gate is arbitrary JSONata, so this does not parse it. It splits the TOP-LEVEL <c>and</c> conjuncts
/// (depth 0, outside string literals), re-evaluates each one alone through the same engine, and names the ones
/// that came back false — which is precise for the <see cref="IdmGateConversion"/> shape every seeded gate uses,
/// and degrades to quoting the whole expression for anything it cannot decompose.
/// </summary>
public static class GateDiagnostics
{
    private const string Prefix = "Eligibility gate not satisfied";
    private const int MaxExpressionEcho = 300;

    // The IdmGateConversion term shape: `(<path> != null and $trim($string(<path>)) != "")`.
    private static readonly Regex LeadingPath =
        new(@"^\(?\s*(?<path>[A-Za-z_$][\w.$]*)\s*!=\s*null\b", RegexOptions.Compiled);

    /// <summary>
    /// Describes why <paramref name="gateExpr"/> did not pass for <paramref name="snapshot"/>. Callers should only
    /// invoke this on the failure path — it re-evaluates the expression a few times.
    /// </summary>
    public static string Describe(IEligibilityGate gate, string? gateExpr, object snapshot)
    {
        // A blank gate means "no gate" (R10) and never reaches here; treat it as unexplained rather than assert.
        if (string.IsNullOrWhiteSpace(gateExpr)) return $"{Prefix}.";

        var terms = SplitTopLevelConjuncts(gateExpr);
        if (terms.Count > 1)
        {
            var failing = terms.Where(t => !gate.IsSatisfied(t, snapshot)).Select(Label).ToList();
            if (failing.Count > 0)
                return $"{Prefix} — missing or empty: {string.Join(", ", failing)}. Gate: {Echo(gateExpr)}";
        }

        // Single term, an `or` in the mix, or every conjunct passes alone while the whole is false (a gate doing
        // something cleverer than a conjunction of presence checks). Quote it rather than guess.
        return $"{Prefix}: {Echo(gateExpr)}";
    }

    /// <summary>The failing term's subject path when it has the canonical shape, else the term text itself.</summary>
    private static string Label(string term)
    {
        var m = LeadingPath.Match(term.Trim());
        return m.Success ? m.Groups["path"].Value : Echo(term);
    }

    /// <summary>
    /// Splits on <c>and</c> at paren depth 0, outside string literals. Returns a single-element list when the
    /// expression contains a top-level <c>or</c> — <c>and</c> binds tighter, so a conjunct lifted out of a
    /// disjunction would be evaluated out of context and could name an innocent term.
    /// </summary>
    public static List<string> SplitTopLevelConjuncts(string expr)
    {
        var whole = new List<string> { expr };
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char? quote = null;

        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];

            if (quote is not null)
            {
                current.Append(c);
                if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '"' or '\'': quote = c; current.Append(c); continue;
                case '(' or '[' or '{': depth++; current.Append(c); continue;
                case ')' or ']' or '}': depth--; current.Append(c); continue;
            }

            if (depth == 0 && IsKeywordAt(expr, i, "or")) return whole;
            if (depth == 0 && IsKeywordAt(expr, i, "and"))
            {
                parts.Add(current.ToString());
                current.Clear();
                i += "and".Length - 1;
                continue;
            }
            current.Append(c);
        }
        if (depth != 0 || quote is not null) return whole;   // unbalanced — do not pretend to understand it

        parts.Add(current.ToString());
        parts = parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 1 ? parts : whole;
    }

    /// <summary>True when <paramref name="word"/> sits at <paramref name="i"/> as a whole word.</summary>
    private static bool IsKeywordAt(string s, int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        var before = i == 0 || !char.IsLetterOrDigit(s[i - 1]) && s[i - 1] != '_';
        var afterIdx = i + word.Length;
        var after = afterIdx >= s.Length || !char.IsLetterOrDigit(s[afterIdx]) && s[afterIdx] != '_';
        return before && after;
    }

    /// <summary>Whitespace-collapsed, length-capped echo — lastError is nvarchar(max) but the grid cell is not.</summary>
    private static string Echo(string expr)
    {
        var flat = Regex.Replace(expr.Trim(), @"\s+", " ");
        return flat.Length <= MaxExpressionEcho ? flat : flat[..MaxExpressionEcho] + "…";
    }
}
