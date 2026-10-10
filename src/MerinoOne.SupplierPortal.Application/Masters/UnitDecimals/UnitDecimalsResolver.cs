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
/// <para>A deactivated unit still resolves — its decimal places keep historical documents rendering unchanged. Within one
/// scope a code is unique (UQ_Unit_company_code); where the company filter is bypassed (system principal) the same code can
/// exist in several companies, and the active unit wins, then the earliest created.</para>
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
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Distinct (case-insensitive) non-blank codes, spelled as the caller passed them — they become the result keys.
        var requested = unitCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0) return result;

        // Upper-case on BOTH sides so the match is case-insensitive whatever the database collation is.
        var wanted = requested.Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();

        var rows = await _db.Units
            .Where(u => wanted.Contains(u.Code.ToUpper()))
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.Seq)
            .Select(u => new { u.Code, u.DecimalPlaces })
            .ToListAsync(ct);

        var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) known.TryAdd(r.Code.Trim(), r.DecimalPlaces);   // rows are ordered: first one wins

        foreach (var code in requested)
            result[code] = known.TryGetValue(code.Trim(), out var places) ? places : Fallback;
        return result;
    }
}
