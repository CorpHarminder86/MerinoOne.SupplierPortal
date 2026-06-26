namespace MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;

/// <summary>
/// R4 (2026-06-26) — Decision D3. Strongly-typed reader for the Fulfilment settings category.
/// </summary>
public interface IFulfilmentSettings
{
    /// <summary>
    /// D3 — when <c>true</c> the over-ship CEILING is enforced (ship qty beyond order qty + tolerance is rejected).
    /// Defaults to <c>false</c> when unset/invalid (the cumulative is tracked either way).
    /// </summary>
    bool EnforceOverShipGuard { get; }
}
