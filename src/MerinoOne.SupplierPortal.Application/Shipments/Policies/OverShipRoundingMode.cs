namespace MerinoOne.SupplierPortal.Application.Shipments.Policies;

/// <summary>
/// R4 (2026-06-30) — over-ship allowance rounding mode (Fulfilment setting <c>OverShipAllowanceRounding</c>).
/// Applied through the SINGLE chokepoint <see cref="OverShipTolerance.RoundAllowance"/> so the displayed
/// allowance, the AsnDetail client cap, and BOTH server guards (create + update) agree on the same cap.
/// </summary>
public enum OverShipRoundingMode
{
    /// <summary>No rounding — the raw fractional allowance. Default; rollout-safe (zero behaviour change).</summary>
    None = 0,

    /// <summary>Round DOWN to a whole unit (2.3 → 2). SAFE: never grants more than the configured tolerance %.</summary>
    Floor,

    /// <summary>Round half-up away from zero (2.5 → 3). May grant up to half a unit beyond the tolerance.</summary>
    Round,

    /// <summary>Round UP to a whole unit (2.3 → 3). GRANTS MORE than the configured tolerance % — use with care.</summary>
    Ceiling,
}
