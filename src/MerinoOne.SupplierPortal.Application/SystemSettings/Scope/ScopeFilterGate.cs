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
    private bool? _enabled;

    public ScopeFilterGate(IServiceScopeFactory scopeFactory, ILogger<ScopeFilterGate> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool FiltersEnabled
    {
        get
        {
            if (_enabled.HasValue) return _enabled.Value;
            lock (_loadLock)
            {
                if (_enabled.HasValue) return _enabled.Value;
                _enabled = Load();
                return _enabled.Value;
            }
        }
    }

    private bool Load()
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
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Pre-migration / design-time / startup race — fail OPEN (filters bypassed) so tooling and
            // the backfill are never blocked. A real runtime read always succeeds.
            _logger.LogWarning(ex, "ScopeFilterGate load failed; defaulting Scope.FiltersEnabled = false (filters bypassed).");
            return false;
        }
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _enabled = null; }
        }
    }
}
