using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;

/// <summary>
/// Singleton reader for SupplierInvite settings. Same caching contract as
/// <see cref="EmailConfig.EmailConfigService"/>: load on first read, invalidate via
/// <see cref="ISettingsCacheInvalidator.InvalidateCategory"/>.
/// </summary>
public class SupplierInviteSettingsService : ISupplierInviteSettings, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SupplierInviteSeed _seed;
    private readonly ILogger<SupplierInviteSettingsService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, string>? _cache;

    public SupplierInviteSettingsService(
        IServiceScopeFactory scopeFactory,
        SupplierInviteSeed seed,
        ILogger<SupplierInviteSettingsService> logger)
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
                .Where(s => s.Category == SupplierInviteKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToList();
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SupplierInviteSettingsService load failed; using seed defaults.");
        }

        return dict;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, SupplierInviteKeys.Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _cache = null; }
        }
    }

    public int ExpiryDays
    {
        get
        {
            var raw = Snapshot.TryGetValue(SupplierInviteKeys.ExpiryDays, out var v) ? v : "7";
            return int.TryParse(raw, out var days) && days > 0 ? days : 7;
        }
    }
}
