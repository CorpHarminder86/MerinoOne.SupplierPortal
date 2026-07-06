using System.Text.Json;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm;

/// <summary>
/// R9 (TSD R9 §2.11, D-R9-16) — converts the legacy R8 gate shape (a JSON array of required-non-null
/// snapshot dot-paths) into the equivalent JSONata boolean expression. Per path the term
/// <c>(p != null and $trim($string(p)) != "")</c> reproduces the old <c>IsNullOrWhiteSpace</c> semantics
/// EXACTLY under the strict-true engine: a missing path makes the comparison undefined → not-true (fail
/// closed); an explicit null fails the first term; whitespace-only fails the $trim; numeric values
/// stringify and pass. The 0049 migration applies the SQL equivalent (OPENJSON + STRING_AGG) to stored
/// rows; this helper is the single source for seeds, the UI's new-row default, and the equivalence tests.
/// </summary>
public static class IdmGateConversion
{
    public static string ToJsonata(IEnumerable<string> paths)
    {
        var terms = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Select(p => $"({p} != null and $trim($string({p})) != \"\")")
            .ToList();
        // An empty gate stays empty — the engine fails closed on a blank expression, matching the old
        // JsonPath gate's empty-array behaviour (never satisfied).
        return terms.Count == 0 ? string.Empty : string.Join(" and ", terms);
    }

    /// <summary>Converts a stored legacy JSON-array gate value; non-array/malformed input returns it unchanged (already an expression).</summary>
    public static string ConvertStoredValue(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        var trimmed = stored.TrimStart();
        if (!trimmed.StartsWith('[')) return stored;   // already a JSONata expression
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(stored);
            return paths is null ? stored : ToJsonata(paths);
        }
        catch (JsonException)
        {
            return stored;   // malformed legacy JSON — engine fails closed, identical to today
        }
    }
}
