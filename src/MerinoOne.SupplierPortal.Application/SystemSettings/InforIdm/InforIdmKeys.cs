namespace MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;

/// <summary>
/// R8 (2026-07-04) — TSD R8. Tenant-wide runtime knobs for the IDM document-sync dispatcher. Read live by the
/// hosted worker each drain (Save-invalidated), so operators can tune cadence/backoff without a redeploy.
/// </summary>
public static class InforIdmKeys
{
    public const string Category = "InforIdm";

    /// <summary>Dispatcher poll interval in seconds (default 10). Range 5–300.</summary>
    public const string DispatcherPollSeconds = "DispatcherPollSeconds";

    /// <summary>Rows claimed per drain pass (default 25). Range 1–500.</summary>
    public const string BatchSize = "BatchSize";

    /// <summary>Max concurrent in-flight dispatches (default 4 — each holds a base64 file in memory). Range 1–16.</summary>
    public const string ConcurrencyCap = "ConcurrencyCap";

    /// <summary>Exponential backoff base in seconds (default 30). Range 5–600.</summary>
    public const string RetryBackoffBaseSeconds = "RetryBackoffBaseSeconds";

    /// <summary>Exponential backoff cap in seconds (default 3600). Must be ≥ base.</summary>
    public const string RetryBackoffCapSeconds = "RetryBackoffCapSeconds";

    /// <summary>Attempts before a transient failure becomes terminal Failed (default 8). Range 1–20.</summary>
    public const string MaxAttempts = "MaxAttempts";

    /// <summary>Stale-InFlight recovery threshold in minutes (default 5). Range 1–120.</summary>
    public const string StaleInFlightMinutes = "StaleInFlightMinutes";
}
