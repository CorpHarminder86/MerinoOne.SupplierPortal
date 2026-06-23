using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Scope;

/// <summary>
/// Singleton reader for the <c>Scope.FiltersEnabled</c> gate. Mirrors
/// <see cref="EmailConfig.EmailConfigService"/>: load on first read via a short-lived scope, cache the
/// value, and drop the snapshot on <see cref="InvalidateCategory"/> so a future admin "go-live" toggle
/// (or the backfill flip) is picked up on the next read. The load uses <c>IgnoreQueryFilters()</c>, so
/// the gate's own query never evaluates the scope filters — no recursion, no second-operation error.
/// </summary>
public sealed class ScopeFilterGate : IScopeFilterGate, ISettingsCacheInvalidator
{
    public const string Category = "Scope";
    public const string Key = "FiltersEnabled";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScopeFilterGate> _logger;

    private readonly object _loadLock = new();
    private ScopeFilterState? _state;

    public ScopeFilterGate(IServiceScopeFactory scopeFactory, ILogger<ScopeFilterGate> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ScopeFilterState State
    {
        get
        {
            // Cache only a DEFINITIVE result (Disabled/Enabled). Unknown (a transient read failure) is never
            // cached, so the next access retries instead of pinning the gate open/closed on a one-off blip.
            var cached = _state;
            if (cached is ScopeFilterState.Disabled or ScopeFilterState.Enabled) return cached.Value;
            lock (_loadLock)
            {
                if (_state is ScopeFilterState.Disabled or ScopeFilterState.Enabled) return _state.Value;
                var loaded = Load();
                if (loaded is ScopeFilterState.Disabled or ScopeFilterState.Enabled) _state = loaded;
                return loaded;
            }
        }
    }

    private ScopeFilterState Load()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var value = db.SystemSettings
                .IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && s.Category == Category && s.SettingKey == Key)
                .Select(s => s.SettingValue)
                .FirstOrDefault();
            // Read succeeded: "true" => Enabled (enforce); missing/anything-else => Disabled (backfill window).
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                ? ScopeFilterState.Enabled
                : ScopeFilterState.Disabled;
        }
        catch (Exception ex)
        {
            // Read FAILED — we don't know the rollout state. Return Unknown (NOT cached) so tenant filtering
            // FAILS CLOSED for tenant-bearing principals until the setting is readable again. (Design-time /
            // pre-migration tooling has a null gate in AppDbContext, which is treated as Disabled separately.)
            _logger.LogWarning(ex, "ScopeFilterGate load failed; Scope.FiltersEnabled is UNKNOWN — tenant/company filters will FAIL CLOSED until readable.");
            return ScopeFilterState.Unknown;
        }
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _state = null; }
        }
    }
}
