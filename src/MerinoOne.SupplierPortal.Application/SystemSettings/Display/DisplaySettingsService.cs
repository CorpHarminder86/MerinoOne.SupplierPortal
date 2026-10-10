using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Display;

/// <summary>
/// Singleton cached reader for the Display settings category. Same caching contract as
/// <see cref="Fulfilment.FulfilmentSettingsService"/>: seed defaults overlaid with the ACTIVE rows, loaded through a
/// short-lived scope on first read, dropped via <see cref="ISettingsCacheInvalidator.InvalidateCategory"/> when a
/// Save/Reset touches the category, and a fall-back to the seed defaults if the DB read fails.
///
/// <para>Two deliberate differences. (1) A load that FAILED is returned but not cached (as
/// <see cref="Scope.ScopeFilterGate"/> does), so the next call retries instead of pinning the defaults until a
/// restart. (2) A load that an invalidation overtook while it was in flight is not cached either, so a Save can
/// never be undone by a stale read finishing late — forced by the reader being async: the synchronous readers get
/// that for free from holding their lock across the load, which an <c>await</c> cannot do.</para>
/// </summary>
public class DisplaySettingsService : IDisplaySettings, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DisplaySeed _seed;
    private readonly ILogger<DisplaySettingsService> _logger;

    private readonly object _gate = new();
    private ConcurrentDictionary<string, string>? _cache;
    private int _generation;

    public DisplaySettingsService(
        IServiceScopeFactory scopeFactory,
        DisplaySeed seed,
        ILogger<DisplaySettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _seed = seed;
        _logger = logger;
    }

    public async Task<int> GetMoneyDecimalPlacesAsync(CancellationToken ct)
    {
        var snapshot = await GetSnapshotAsync(ct);
        return snapshot.TryGetValue(DisplayKeys.MoneyDecimalPlaces, out var raw)
               && DisplaySeed.TryParseMoneyDecimalPlaces(raw, out var places)
            ? places
            : AppMoney.DefaultDecimals;
    }

    private async Task<ConcurrentDictionary<string, string>> GetSnapshotAsync(CancellationToken ct)
    {
        int generation;
        lock (_gate)
        {
            if (_cache != null) return _cache;
            generation = _generation;
        }

        var (loaded, definitive) = await LoadAsync(ct);

        if (definitive)
        {
            lock (_gate)
            {
                if (_generation == generation) _cache = loaded;
            }
        }

        return loaded;
    }

    private async Task<(ConcurrentDictionary<string, string> Values, bool Definitive)> LoadAsync(CancellationToken ct)
    {
        var dict = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _seed.Defaults) dict[kv.Key] = kv.Value;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = await db.SystemSettings
                .Where(s => s.Category == DisplayKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToListAsync(ct);
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
            return (dict, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DisplaySettingsService load failed; using seed defaults.");
            return (dict, false);
        }
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, DisplayKeys.Category, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _generation++;
                _cache = null;
            }
        }
    }
}
