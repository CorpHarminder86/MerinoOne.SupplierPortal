namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// R5 (TSD R5 Addendum §12 / Component 8) — writes a row to the inbound integration <c>proc.SyncLog</c> so an
/// admin can see every inbound call's outcome and, on failure, its error message (§12.2). One row per inbound
/// call attempt: <see cref="WriteSuccessAsync"/> on a clean run, <see cref="WriteFailedAsync"/> on a
/// rejected / exception path. The same log is where the unresolvable-ship-to (§6.2) and unmapped-status
/// (§11.3) failures land.
///
/// <para><b>SCALABILITY — payload only on FAILED rows.</b> This DB runs on SQL Express with a 10 GB cap.
/// Storing the raw inbound payload on EVERY success row would blow that cap fast (high-volume PO/GRN inbound).
/// So <see cref="WriteSuccessAsync"/> NEVER stores a payload; only <see cref="WriteFailedAsync"/> persists the
/// raw payload (for diagnosis / replay of the failure). Retention: failed-row payloads are the operator's
/// system-of-record for inbound drift; they are pruned by the integration-log retention job, NOT kept
/// indefinitely. Success rows are tiny (no payload) and are kept for the traceability trail (§12.2).</para>
/// </summary>
public interface ISyncLogWriter
{
    /// <summary>
    /// Records a SUCCESSFUL inbound call. NO payload is stored (scalability — see the type doc). The row is
    /// tenant-scoped to <paramref name="tenantId"/> (falls back to the writer's current-user tenant when null).
    /// Persists immediately via its own SaveChanges unless <paramref name="defer"/> is true (then the row is
    /// only tracked — the caller's transaction commits it; used by the inbound executor so the SyncLog row
    /// participates in the same atomic flush).
    /// </summary>
    Task WriteSuccessAsync(string api, string? entityType, string? externalRef,
        Guid? tenantId = null, bool defer = false, CancellationToken ct = default);

    /// <summary>
    /// Records a FAILED inbound call with its human-readable <paramref name="errorMessage"/> (§12.2) and the
    /// raw <paramref name="payload"/> (stored for diagnosis — ONLY failed rows carry a payload). Tenant-scoped
    /// to <paramref name="tenantId"/> (falls back to the current-user tenant). <paramref name="defer"/> as in
    /// <see cref="WriteSuccessAsync"/>.
    /// </summary>
    Task WriteFailedAsync(string api, string? entityType, string? externalRef, string errorMessage,
        string? payload, Guid? tenantId = null, bool defer = false, CancellationToken ct = default);
}
