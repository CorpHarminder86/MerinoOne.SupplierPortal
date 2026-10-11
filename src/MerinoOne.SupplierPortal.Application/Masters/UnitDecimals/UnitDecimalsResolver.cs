using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;

/// <summary>
/// <see cref="IUnitDecimalsResolver"/> over <c>mdm.Unit</c>.
///
/// <para>Reads <c>db.Units</c> THROUGH the normal query filters, like the Unit master handlers: Unit is company-scoped and
/// sharing-aware, so the caller sees the units of its active company's source company — the master the PO's unit code was
/// resolved against on the inbound push. (Unlike <c>TaxRateResolver</c> there is no IgnoreQueryFilters: a unit is looked up by
/// CODE inside the viewer's own scope, not by an id that may point at an unshared source company.)</para>
///
/// <para><b>Unit codes are case-sensitive</b> (<c>mdm.Unit.code</c> carries a case-sensitive collation since migration 0063:
/// "PCS" and "pcs" are two units, each with its own decimal places), so a requested code resolves in this order:
/// <list type="number">
/// <item>the unit spelled EXACTLY like the code (ordinal);</item>
/// <item>else, when exactly ONE spelling of the code exists in the master, that unit — a PO line saying "pcs" while only
/// "PCS" is defined (a document or an ERP feed that case-shifted the unit) keeps rendering at the master's precision;</item>
/// <item>else — the code is unknown, or ambiguous (several spellings exist and none is the exact one) — the fallback (3).</item>
/// </list>
/// ONE query serves all three: it fetches every unit whose upper-cased code is one of the requested codes upper-cased (the
/// exact match plus its case-variants) and the choice is made in memory. The result is keyed by the code AS REQUESTED, ordinal —
/// "PCS" and "pcs" are separate entries.</para>
///
/// <para>A deactivated unit still resolves — its decimal places keep historical documents rendering unchanged. Within one
/// scope a spelling of a code is unique (UQ_Unit_company_code); where the company filter is bypassed (system principal) the same
/// spelling can exist in several companies, and the active unit wins, then the earliest created.</para>
/// </summary>
public sealed class UnitDecimalsResolver : IUnitDecimalsResolver
{
    private const int FallbackDecimalPlaces = 3;

    private readonly IAppDbContext _db;
    public UnitDecimalsResolver(IAppDbContext db) => _db = db;

    public int Fallback => FallbackDecimalPlaces;

    public async Task<int> ResolveAsync(string? unitCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(unitCode)) return Fallback;   // nothing to look up — no query

        var byCode = await ResolveAsync(new[] { unitCode }, ct);
        return byCode.TryGetValue(unitCode, out var places) ? places : Fallback;
    }

    public async Task<IReadOnlyDictionary<string, int>> ResolveAsync(IEnumerable<string?> unitCodes, CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        // Distinct (ordinal) non-blank codes, spelled as the caller passed them — they become the result keys.
        var requested = unitCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0) return result;

        // The column is case-sensitive, so upper-case BOTH sides: this returns each requested code's exact match AND its
        // case-variants, which the in-memory pass below tells apart.
        var wanted = requested.Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();

        var rows = await _db.Units
            .Where(u => wanted.Contains(u.Code.ToUpper()))
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.Seq)
            .Select(u => new { u.Code, u.DecimalPlaces })
            .ToListAsync(ct);

        // spelling → decimal places (rows are ordered, so the first row of a spelling wins), and
        // upper-cased code → the distinct spellings that exist for it.
        var bySpelling = new Dictionary<string, int>(StringComparer.Ordinal);
        var spellingsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var spelling = r.Code.Trim();
            if (!bySpelling.TryAdd(spelling, r.DecimalPlaces)) continue;

            var upper = spelling.ToUpperInvariant();
            if (!spellingsOf.TryGetValue(upper, out var spellings)) spellingsOf[upper] = spellings = new List<string>();
            spellings.Add(spelling);
        }

        foreach (var code in requested)
        {
            var trimmed = code.Trim();
            if (bySpelling.TryGetValue(trimmed, out var exact))
                result[code] = exact;                                                       // 1. exact
            else if (spellingsOf.TryGetValue(trimmed.ToUpperInvariant(), out var variants) && variants.Count == 1)
                result[code] = bySpelling[variants[0]];                                     // 2. the one case-variant
            else
                result[code] = Fallback;                                                    // 3. unknown / ambiguous
        }
        return result;
    }
}
