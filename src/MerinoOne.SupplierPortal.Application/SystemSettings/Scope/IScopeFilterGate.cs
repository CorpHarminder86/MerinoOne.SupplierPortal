namespace MerinoOne.SupplierPortal.Application.SystemSettings.Scope;

/// <summary>
/// Tri-state of the scope-filter rollout gate (<c>Scope.FiltersEnabled</c> SystemSetting):
/// <list type="bullet">
///   <item><b>Disabled</b> — the setting was read and is missing/false: the legitimate backfill window;
///         tenant/company filters are BYPASSED so legacy NULL-scope rows stay visible.</item>
///   <item><b>Enabled</b> — the setting was read and is "true": filters ENFORCE.</item>
///   <item><b>Unknown</b> — the setting could NOT be read (transient failure): we do not know the rollout
///         state, so tenant filtering FAILS CLOSED for any principal that carries a tenant (deny, don't leak).</item>
/// </list>
/// </summary>
public enum ScopeFilterState { Disabled, Enabled, Unknown }

/// <summary>
/// Singleton gate for the always-on tenant + company query filters. Reads the
/// <c>Scope.FiltersEnabled</c> SystemSetting (cached) via its own scope — NEVER from the
/// request DbContext — so the per-query filter predicate can read it with zero database I/O and
/// no re-entrancy ("a second operation was started on this context") during query evaluation.
/// A definitive result (Disabled/Enabled) is cached; <see cref="ScopeFilterState.Unknown"/> is NOT cached
/// so a transient read failure retries on the next access instead of pinning the gate open.
/// </summary>
public interface IScopeFilterGate
{
    ScopeFilterState State { get; }
}
