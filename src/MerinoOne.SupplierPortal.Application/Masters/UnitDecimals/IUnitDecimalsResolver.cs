namespace MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;

/// <summary>
/// Resolves a unit-of-measure CODE (a PO line's <c>OrderUnit</c>, an ASN line's, ...) to the number of decimal places
/// the <c>mdm.Unit</c> master defines for it, so the read DTOs can carry <c>UnitDecimalPlaces</c> and the UI can format
/// and validate quantities at the right precision.
///
/// <para>Codes match exactly first, then by a unique case-insensitive match. An unknown, null or blank code resolves to <see cref="Fallback"/> (3) — never an
/// error. Prefer the batch overload for any list/detail projection: it costs ONE query however many lines or codes are
/// passed. (A bare <c>null</c> literal is ambiguous between the two overloads — pass a typed <c>string?</c>.)</para>
/// </summary>
public interface IUnitDecimalsResolver
{
    /// <summary>The decimal places used when a unit code is null, blank or not found in the unit master (3).</summary>
    int Fallback { get; }

    /// <summary>Decimal places for one unit code; <see cref="Fallback"/> when it is null, blank or unknown.</summary>
    Task<int> ResolveAsync(string? unitCode, CancellationToken ct);

    /// <summary>
    /// Decimal places for many unit codes in ONE query. The returned dictionary has ordinal keys (exact match first, then a unique case-insensitive match) and holds an
    /// entry for every distinct non-blank requested code (an unknown code maps to <see cref="Fallback"/>); null/blank
    /// codes are omitted — use <see cref="Fallback"/> for those.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> ResolveAsync(IEnumerable<string?> unitCodes, CancellationToken ct);
}
