namespace MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum, Decision D3. Tenant-wide fulfilment-control settings category.
/// </summary>
public static class FulfilmentKeys
{
    public const string Category = "Fulfilment";

    /// <summary>
    /// D3 — gates the over-ship CEILING REJECTION on ASN create/update. <c>false</c> by default (rollout control):
    /// off in prod until tolerances are seeded, then enabled. The cumulative <c>shippedQtyToDate</c> is ALWAYS
    /// maintained regardless of this flag — only the ceiling rejection is gated.
    /// </summary>
    public const string EnforceOverShipGuard = "EnforceOverShipGuard";

    /// <summary>
    /// R4 (2026-06-30) — rounds the over-ship ALLOWANCE to a whole unit (for discrete UOMs). One of
    /// <c>None|Floor|Round|Ceiling</c>, default <c>None</c> (no change, rollout-safe). <c>Floor</c> is the safe
    /// production choice (never grants more than the configured tolerance %); <c>Ceiling</c> grants MORE than the
    /// configured %. Applied through the single chokepoint <c>OverShipTolerance.RoundAllowance</c> at every site.
    /// </summary>
    public const string OverShipAllowanceRounding = "OverShipAllowanceRounding";
}
