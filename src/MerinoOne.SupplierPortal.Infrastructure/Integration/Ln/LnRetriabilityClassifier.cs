namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.3, D-R9-5) — the code-owned permanent-vs-retriable classification for the dynamic LN path.
/// 4xx = permanent (the request is wrong; retrying the same bytes cannot succeed); 5xx / timeout / transport
/// = retriable (LN or the network was unhappy; the deterministic key makes the retry safe).
///
/// Flip-proof BY CONSTRUCTION: the API takes only the HTTP status code — a mapping expression enriches error
/// TEXT elsewhere and has no input into this class. Do not add a string/expression parameter here.
/// </summary>
public static class LnRetriabilityClassifier
{
    /// <summary>True when the failure is permanent (4xx). 5xx and anything non-HTTP (timeout, transport) is retriable.</summary>
    public static bool IsPermanent(int? httpStatusCode)
        => httpStatusCode is >= 400 and < 500;

    /// <summary>Stable marker persisted on IntegrationError.StackTrace for permanent dynamic-path failures (drives the re-arm warning badge).</summary>
    public const string PermanentErrorMarker = "ln-permanent-4xx";

    /// <summary>Prefix stamped onto OutboxMessage.LastError for permanent failures (no schema change in Phase A).</summary>
    public const string PermanentLastErrorPrefix = "[permanent] ";
}
