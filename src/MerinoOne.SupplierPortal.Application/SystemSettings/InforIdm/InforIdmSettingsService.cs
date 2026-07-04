using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;

/// <summary>
/// R8 (2026-07-04) — singleton cached reader for the InforIdm dispatcher settings. Same caching contract as
/// <see cref="Fulfilment.FulfilmentSettingsService"/>: load on first read, invalidate via
/// <see cref="ISettingsCacheInvalidator.InvalidateCategory"/> on Save/Reset; seed-default fallback on DB error.
/// The hosted worker reads it each drain so knob changes take effect live.
/// </summary>
public class InforIdmSettingsService : IInforIdmSettings, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InforIdmSeed _seed;
    private readonly ILogger<InforIdmSettingsService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, string>? _cache;

    public InforIdmSettingsService(IServiceScopeFactory scopeFactory, InforIdmSeed seed, ILogger<InforIdmSettingsService> logger)
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
                .Where(s => s.Category == InforIdmKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToList();
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InforIdmSettingsService load failed; using seed defaults.");
        }

        return dict;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, InforIdmKeys.Category, StringComparison.Ordinal))
            lock (_loadLock) { _cache = null; }
    }

    private int Read(string key, int fallback)
        => Snapshot.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public int DispatcherPollSeconds => Read(InforIdmKeys.DispatcherPollSeconds, 10);
    public int BatchSize => Read(InforIdmKeys.BatchSize, 25);
    public int ConcurrencyCap => Read(InforIdmKeys.ConcurrencyCap, 4);
    public int RetryBackoffBaseSeconds => Read(InforIdmKeys.RetryBackoffBaseSeconds, 30);
    public int RetryBackoffCapSeconds => Read(InforIdmKeys.RetryBackoffCapSeconds, 3600);
    public int MaxAttempts => Read(InforIdmKeys.MaxAttempts, 8);
    public int StaleInFlightMinutes => Read(InforIdmKeys.StaleInFlightMinutes, 5);
}
