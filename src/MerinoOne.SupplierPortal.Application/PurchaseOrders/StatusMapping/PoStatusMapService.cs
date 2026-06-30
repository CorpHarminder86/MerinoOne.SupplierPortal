using System.Collections.Concurrent;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.StatusMapping;

/// <summary>
/// R5 (TSD R5 Addendum §11 / Component 7) — singleton cached reader for the ERP→portal PO-status mapping
/// master (<c>proc.PoStatusMapping</c>). Same caching contract as
/// <see cref="MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment.FulfilmentSettingsService"/>:
/// load on first read, invalidate via <see cref="ISettingsCacheInvalidator.InvalidateCategory"/> when a
/// Save/Delete touches the mapping category. Unlike the SystemSettings readers this caches PER-TENANT (the
/// map is tenant-scoped), so the singleton holds <c>tenantId → (erpStatus → PoStatus)</c>, each inner
/// dictionary CASE-INSENSITIVE (<see cref="StringComparer.OrdinalIgnoreCase"/>) per §4.7.
///
/// <para>The DB read runs under the singleton's own scope (no user principal), so it bypasses the seccode RLS
/// + tenant query filters with <c>IgnoreQueryFilters()</c> and loads every tenant's ACTIVE, non-deleted rows
/// in one pass. Resolution is then an in-memory dictionary lookup (zero DB I/O on the inbound hot path).</para>
/// </summary>
public class PoStatusMapService : IPoStatusMap, ISettingsCacheInvalidator
{
    /// <summary>The invalidation category key fan-out uses when a mapping row is saved/deleted.</summary>
    public const string Category = "PoStatusMapping";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PoStatusMapService> _logger;

    private readonly object _loadLock = new();
    private ConcurrentDictionary<Guid, Dictionary<string, PoStatus>>? _cache;

    public PoStatusMapService(IServiceScopeFactory scopeFactory, ILogger<PoStatusMapService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private ConcurrentDictionary<Guid, Dictionary<string, PoStatus>> Snapshot
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

    private ConcurrentDictionary<Guid, Dictionary<string, PoStatus>> Load()
    {
        var byTenant = new ConcurrentDictionary<Guid, Dictionary<string, PoStatus>>();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            // Singleton scope has no user principal — bypass the seccode RLS + tenant filters and load every
            // tenant's active, non-deleted mapping rows in one pass.
            var rows = db.PoStatusMappings.IgnoreQueryFilters()
                .Where(m => !m.IsDeleted && m.IsActive && m.TenantId != null)
                .Select(m => new { m.TenantId, m.ErpStatus, m.PoStatus })
                .ToList();

            foreach (var r in rows)
            {
                if (r.TenantId is not Guid tid) continue;
                if (string.IsNullOrWhiteSpace(r.ErpStatus)) continue;
                if (!Enum.TryParse<PoStatus>(r.PoStatus, ignoreCase: true, out var target)) continue;

                var inner = byTenant.GetOrAdd(tid, _ => new Dictionary<string, PoStatus>(StringComparer.OrdinalIgnoreCase));
                // The filtered UQ (tenantId, erpStatus) makes this deterministic; last-wins guards any legacy dup.
                inner[r.ErpStatus.Trim()] = target;
            }
        }
        catch (Exception ex)
        {
            // No seed fallback: an UNMAPPED status is the SAFE, visible outcome (§11.3) — a failed load simply
            // resolves everything to UNMAPPED (Sync Log Failed rows), never a silent guess.
            _logger.LogWarning(ex, "PoStatusMapService load failed; all statuses will resolve as UNMAPPED until reloaded.");
        }
        return byTenant;
    }

    public void InvalidateCategory(string category)
    {
        if (string.Equals(category, Category, StringComparison.Ordinal))
        {
            lock (_loadLock) { _cache = null; }
        }
    }

    public PoStatus? Resolve(Guid? tenantId, string? erpStatus)
    {
        if (tenantId is not Guid tid) return null;
        if (string.IsNullOrWhiteSpace(erpStatus)) return null;
        if (!Snapshot.TryGetValue(tid, out var inner)) return null;
        return inner.TryGetValue(erpStatus.Trim(), out var status) ? status : null;
    }
}
