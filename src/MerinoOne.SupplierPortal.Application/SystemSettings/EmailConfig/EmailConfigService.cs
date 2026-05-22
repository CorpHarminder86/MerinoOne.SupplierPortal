using System.Collections.Concurrent;
using System.Security.Cryptography;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;

/// <summary>
/// Singleton reader for EmailConfig. Loads all seven keys on first access and caches them
/// in a <see cref="ConcurrentDictionary{TKey, TValue}"/>; <see cref="InvalidateCategory"/>
/// nukes the snapshot so the next property read pulls fresh values via a scoped DbContext.
/// </summary>
public class EmailConfigService : IEmailConfig, ISettingsCacheInvalidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingProtector _protector;
    private readonly EmailConfigSeed _seed;
    private readonly ILogger<EmailConfigService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<string, string>? _cache;

    public EmailConfigService(
        IServiceScopeFactory scopeFactory,
        ISettingProtector protector,
        EmailConfigSeed seed,
        ILogger<EmailConfigService> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
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

        // Pre-populate with seed defaults so a missing DB row never throws.
        foreach (var kv in _seed.Defaults) dict[kv.Key] = kv.Value;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rows = db.SystemSettings
                .Where(s => s.Category == EmailConfigKeys.Category && s.IsActive)
                .Select(s => new { s.SettingKey, s.SettingValue })
                .ToList();
            foreach (var r in rows) dict[r.SettingKey] = r.SettingValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            // Pre-startup or migration race — log and fall through with seed defaults.
            _logger.LogWarning(ex, "EmailConfigService load failed; using seed defaults.");
        }

        return dict;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, EmailConfigKeys.Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _cache = null; }
        }
    }

    private string Get(string key)
        => Snapshot.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

    public string FromAddress => Get(EmailConfigKeys.Email);
    public string Host => Get(EmailConfigKeys.Host);

    public int Port
    {
        get
        {
            var raw = Get(EmailConfigKeys.Port);
            return int.TryParse(raw, out var p) ? p : 587;
        }
    }

    public bool EnableSsl
    {
        get
        {
            var raw = Get(EmailConfigKeys.EnableSsl);
            return !bool.TryParse(raw, out var v) || v; // default true on parse failure
        }
    }

    public bool DefaultCredentials
    {
        get
        {
            var raw = Get(EmailConfigKeys.DefaultCredentials);
            return !bool.TryParse(raw, out var v) || v; // default true
        }
    }

    public string UserName => Get(EmailConfigKeys.UserName);

    public string Password
    {
        get
        {
            var raw = Get(EmailConfigKeys.Password);
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            try
            {
                return _protector.Unprotect(raw);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex, "EmailConfig.Password unprotect failed — returning empty (key-ring rotation?).");
                return string.Empty;
            }
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
