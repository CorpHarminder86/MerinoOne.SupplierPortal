namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §5.5. Pure exponential-backoff schedule for transient IDM failures:
/// <c>delay(attempt) = min(base · 2^(attempt-1), cap)</c>. Attempts beyond <c>maxAttempts</c> are terminal
/// (Failed). Static + deterministic so it is unit-testable without a clock.
/// </summary>
public static class IdmBackoffPolicy
{
    /// <summary>Backoff for the given attempt number (1-based). Guards against overflow at high attempt counts.</summary>
    public static TimeSpan NextDelay(int attempt, int baseSeconds, int capSeconds)
    {
        if (attempt < 1) attempt = 1;
        // 2^(attempt-1) capped early to avoid overflow; anything past ~30 is already at the cap.
        var factor = attempt >= 31 ? long.MaxValue : 1L << (attempt - 1);
        var raw = factor > capSeconds / Math.Max(1, baseSeconds)
            ? capSeconds
            : Math.Min((long)baseSeconds * factor, capSeconds);
        return TimeSpan.FromSeconds(raw);
    }

    /// <summary>True when the attempt count has reached the terminal ceiling.</summary>
    public static bool IsExhausted(int attemptCount, int maxAttempts) => attemptCount >= maxAttempts;
}
