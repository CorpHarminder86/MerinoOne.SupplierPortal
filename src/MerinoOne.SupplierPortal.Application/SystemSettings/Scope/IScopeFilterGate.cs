namespace MerinoOne.SupplierPortal.Application.SystemSettings.Scope;

/// <summary>
/// Singleton gate for the always-on tenant + company query filters. Reads the
/// <c>Scope.FiltersEnabled</c> SystemSetting ONCE (cached) via its own scope — NEVER from the
/// request DbContext — so the per-query filter predicate can read it with zero database I/O and
/// no re-entrancy ("a second operation was started on this context") during query evaluation.
/// Fails OPEN (filters bypassed) when the row is missing/false or unreachable; the tenant + company
/// filters only engage once an operator (the backfill, or a future admin toggle) sets it to "true".
/// </summary>
public interface IScopeFilterGate
{
    bool FiltersEnabled { get; }
}
