namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Threads the deterministic outbound idempotency key from the outbox dispatcher (or retry handler) into the
/// <see cref="IInforIntegrationService"/> implementation WITHOUT changing the (solution-architect-owned) interface
/// signature. The Live service reads the current key and replays it verbatim as the ERP <c>X-Idempotency-Key</c>
/// (so the ERP dedupes); when no key is set (legacy direct calls) it falls back to a freshly-minted key.
///
/// Scoped: set by the dispatcher within the per-message dispatch scope (and by <c>RetryIntegrationErrorCommand</c>
/// within the request scope) immediately before invoking the ERP method, then cleared. This is the fix for D2
/// (random keys per retry) that keeps the interface stub untouched.
/// </summary>
public interface IOutboundIdempotencyContext
{
    /// <summary>The deterministic key the next outbound call should replay, or null to mint a fresh one.</summary>
    string? CurrentKey { get; }

    /// <summary>Sets the deterministic key for the upcoming outbound call.</summary>
    void Set(string deterministicKey);

    /// <summary>Clears the key after the call so a subsequent unrelated call mints its own.</summary>
    void Clear();
}
