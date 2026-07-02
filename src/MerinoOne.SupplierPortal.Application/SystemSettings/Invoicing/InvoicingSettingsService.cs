using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;

/// <summary>
/// R6 (2026-07-02) — plan D11. Singleton cached reader for the Invoicing settings category. Same caching
/// contract as <see cref="Fulfilment.FulfilmentSettingsService"/>: load on first read, invalidate via
/// <see cref="ISettingsCacheInvalidator.InvalidateCategory"/> when a Save/Reset touches the category. Falls back
/// to seed defaults if the DB read fails.
/// </summary>
public class InvoicingSettingsService : IInvoicingSettings, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InvoicingSeed _seed;
    private readonly ILogger<InvoicingSettingsService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, string>? _cache;

    public InvoicingSettingsService(
        IServiceScopeFactory scopeFactory,
        InvoicingSeed seed,
        ILogger<InvoicingSettingsService> logger)
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
                .Where(s => s.Category == InvoicingKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToList();
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InvoicingSettingsService load failed; using seed defaults.");
        }

        return dict;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, InvoicingKeys.Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _cache = null; }
        }
    }

    private bool ReadBool(string key)
    {
        var raw = Snapshot.TryGetValue(key, out var v) ? v : "false";
        return bool.TryParse(raw, out var on) && on;
    }

    public bool RequireIrn => ReadBool(InvoicingKeys.RequireIrn);

    public bool RequireEWayBill => ReadBool(InvoicingKeys.RequireEWayBill);
}
