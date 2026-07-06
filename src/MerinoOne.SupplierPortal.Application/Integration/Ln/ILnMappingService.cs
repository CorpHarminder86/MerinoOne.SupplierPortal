namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.3, D-R9-6) — the shared JSONata engine for LN outbound: request/response/ack mappings and
/// eligibility gates all evaluate through this one service (compiled-expression cache; the Phase D editor
/// live-eval endpoint reuses it). Pure evaluation — no HTTP, no persistence, no status transitions: the
/// code-owned invariants (retriability class, portalRef correlation, status flips) live outside (D-R9-5).
/// </summary>
public interface ILnMappingService
{
    /// <summary>Compile check only. Null = compiles; otherwise the parser error message (surfaced inline at config save).</summary>
    string? ValidateSyntax(string expression);

    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="inputJson"/>. Never throws —
    /// evaluation failures come back as <see cref="LnEvalResult.Error"/>. An expression whose result is
    /// JSONata "nothing" (undefined) yields <c>Ok=true, OutputJson=null</c> — callers decide what absence means
    /// (a gate treats it as not-eligible; a request mapping treats it as a config error).
    /// </summary>
    LnEvalResult Evaluate(string expression, string inputJson);
}

/// <summary>Outcome of one JSONata evaluation. <c>OutputJson</c> is null when the expression produced no value.</summary>
public sealed record LnEvalResult(bool Ok, string? OutputJson, string? Error)
{
    public static LnEvalResult Success(string? outputJson) => new(true, outputJson, null);
    public static LnEvalResult Failure(string error) => new(false, null, error);
}
