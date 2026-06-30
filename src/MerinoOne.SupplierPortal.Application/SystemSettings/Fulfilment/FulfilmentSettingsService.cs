using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;

/// <summary>
/// R4 (2026-06-26) — Decision D3. Singleton cached reader for the Fulfilment settings category. Same caching
/// contract as <see cref="SupplierInvite.SupplierInviteSettingsService"/> / <see cref="EmailConfig.EmailConfigService"/>:
/// load on first read, invalidate via <see cref="ISettingsCacheInvalidator.InvalidateCategory"/> when a Save/Reset
/// touches the category. Falls back to seed defaults if the DB read fails.
/// </summary>
public class FulfilmentSettingsService : IFulfilmentSettings, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FulfilmentSeed _seed;
    private readonly ILogger<FulfilmentSettingsService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, string>? _cache;

    public FulfilmentSettingsService(
        IServiceScopeFactory scopeFactory,
        FulfilmentSeed seed,
        ILogger<FulfilmentSettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _seed = seed;
        _logger = logger;
    }

    private ConcurrentDictionary<string, string> Snapshot
    {
        get
        {
            if (_cache != null) return _cache;
            lock (_loadLock)
            {
                if (_cache != null) return _cache;
                _cache = Load();
                return _cache;
            }
        }
    }

    private ConcurrentDictionary<string, string> Load()
    {
        var dict = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _seed.Defaults) dict[kv.Key] = kv.Value;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = db.SystemSettings
                .Where(s => s.Category == FulfilmentKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToList();
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FulfilmentSettingsService load failed; using seed defaults.");
        }

        return dict;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, FulfilmentKeys.Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _cache = null; }
        }
    }

    public bool EnforceOverShipGuard
    {
        get
        {
            var raw = Snapshot.TryGetValue(FulfilmentKeys.EnforceOverShipGuard, out var v) ? v : "false";
            return bool.TryParse(raw, out var on) && on;
        }
    }

    public OverShipRoundingMode OverShipAllowanceRounding
    {
        get
        {
            var raw = Snapshot.TryGetValue(FulfilmentKeys.OverShipAllowanceRounding, out var v) ? v
                : nameof(OverShipRoundingMode.None);
            return Enum.TryParse<OverShipRoundingMode>(raw, ignoreCase: true, out var m) ? m : OverShipRoundingMode.None;
        }
    }
}
