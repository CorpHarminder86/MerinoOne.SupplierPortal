using System.Collections.Concurrent;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 — <see cref="ILnMappingService"/> on Jsonata.Net.Native. Registered as a SINGLETON with a bounded
/// compiled-expression cache keyed by the normalised expression hash — the dispatcher evaluates the same
/// eight-ish expressions on every drain cycle, and R8's recompile-per-eval was measurable waste.
/// <c>JsonataQuery</c> is immutable after parse and safe for concurrent <c>Eval</c> (verified by the
/// LnMappingService spike test — parallel evals over one cached query).
/// </summary>
public sealed class LnMappingService : ILnMappingService
{
    // The live config corpus is tiny (8 expressions × 3 slots × tenants); 256 absorbs editor drafts.
    // Overflow clears wholesale — recompilation is cheap and correctness never depends on the cache.
    private const int MaxCacheEntries = 256;
    private readonly ConcurrentDictionary<string, JsonataQuery> _cache = new();

    public string? ValidateSyntax(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "Expression is empty.";
        try { _ = new JsonataQuery(expression); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public LnEvalResult Evaluate(string expression, string inputJson)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return LnEvalResult.Failure("Expression is empty.");

        JsonataQuery query;
        try
        {
            var key = ExpressionHash.Compute(expression);
            if (_cache.Count > MaxCacheEntries) _cache.Clear();
            query = _cache.GetOrAdd(key, _ => new JsonataQuery(expression));
        }
        catch (Exception ex)
        {
            return LnEvalResult.Failure($"Expression does not compile: {ex.Message}");
        }

        try
        {
            var result = query.Eval(JToken.Parse(inputJson));
            // JSONata "nothing" (undefined) — the expression navigated off the document. Surface as
            // Ok-with-null; the caller decides what absence means (gate: not eligible; request: config error).
            if (result is null || result.Type == JTokenType.Undefined)
                return LnEvalResult.Success(null);
            return LnEvalResult.Success(result.ToFlatString());
        }
        catch (Exception ex)
        {
            return LnEvalResult.Failure($"Evaluation failed: {ex.Message}");
        }
    }

    /// <summary>Exposed for the spike/unit tests: current compiled-cache entry count.</summary>
    internal int CacheCount => _cache.Count;
}
