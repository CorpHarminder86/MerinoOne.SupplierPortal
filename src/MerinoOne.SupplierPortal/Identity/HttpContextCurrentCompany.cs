using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Identity;

/// <summary>
/// Per-request (scoped) active-company context. Resolves the active company from the
/// <c>X-Active-Company</c> header, validating it against the caller's accessible companies, and
/// falling back to the user's default (or sole) mapped company. Backs the always-on company filter.
///
/// CRITICAL: all DB reads run in a FRESH scope via <see cref="IServiceScopeFactory"/> — NEVER the
/// request <c>AppDbContext</c>. Injecting <c>IAppDbContext</c> here would create a construction cycle
/// (AppDbContext → ICurrentCompany → IAppDbContext → AppDbContext) that deadlocks the DI container at
/// startup, and querying the request context inside the filter path would re-enter the very query being
/// evaluated. The short-lived contexts use <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters"/>
/// so they never recurse back into this resolver. Mirrors EmailConfigService / ScopeFilterGate.
///
/// All three computed sets (accessible companies, active company, sharing map) are memoized for the
/// life of the request so repeated queries don't re-hit the database.
/// </summary>
public class HttpContextCurrentCompany : ICurrentCompany
{
    public const string HeaderName = "X-Active-Company";

    private readonly IHttpContextAccessor _accessor;
    private readonly ICurrentUser _currentUser;
    private readonly IServiceScopeFactory _scopeFactory;

    private bool _accessibleLoaded;
    private IReadOnlyCollection<Guid> _accessible = Array.Empty<Guid>();

    private bool _activeResolved;
    private Guid? _active;

    private bool _fullAccessResolved;
    private bool _fullAccess;

    // member → source map, per endpoint. Lazily loaded once per request.
    private readonly Dictionary<SharedEndpoint, Dictionary<Guid, Guid>> _shareMaps = new();

    public HttpContextCurrentCompany(IHttpContextAccessor accessor, ICurrentUser currentUser, IServiceScopeFactory scopeFactory)
    {
        _accessor = accessor;
        _currentUser = currentUser;
        _scopeFactory = scopeFactory;
    }

    public IReadOnlyCollection<Guid> AccessibleCompanyIds
    {
        get
        {
            if (_accessibleLoaded) return _accessible;
            _accessible = LoadAccessible();
            _accessibleLoaded = true;
            return _accessible;
        }
    }

    public Guid? ActiveCompanyId
    {
        get
        {
            if (_activeResolved) return _active;
            _active = ResolveActive();
            _activeResolved = true;
            return _active;
        }
    }

    public bool ActiveCompanyFullAccess
    {
        get
        {
            if (_fullAccessResolved) return _fullAccess;
            _fullAccess = ResolveFullAccess();
            _fullAccessResolved = true;
            return _fullAccess;
        }
    }

    public Guid? ResolveSource(SharedEndpoint endpoint, Guid? companyId)
    {
        if (companyId is null) return null;
        var map = GetShareMap(endpoint);
        return map.TryGetValue(companyId.Value, out var source) ? source : companyId;
    }

    /// <summary>Run a read against a FRESH DbContext scope (never the request context — see class remarks).</summary>
    private T WithDb<T>(Func<IAppDbContext, T> read)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return read(db);
    }

    private IReadOnlyCollection<Guid> LoadAccessible()
    {
        var tenantId = _currentUser.TenantId;
        if (tenantId is null) return Array.Empty<Guid>();

        // Tenant Admin → every active company in the tenant.
        if (_currentUser.IsAdmin)
        {
            return WithDb(db => (IReadOnlyCollection<Guid>)db.TenantEntities.IgnoreQueryFilters()
                .Where(t => !t.IsDeleted && t.TenantId == tenantId && t.IsActive)
                .Select(t => t.Id)
                .ToArray());
        }

        // Regular user → the "company" JWT claims (minted from UserCompanyMap). Fall back to the DB map
        // for sessions minted before this feature shipped.
        var claimCompanies = (_accessor.HttpContext?.User.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>())
            .Where(c => c.Type == "company")
            .Select(c => Guid.TryParse(c.Value, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToArray();

        if (claimCompanies.Length > 0) return claimCompanies;

        var userCode = _currentUser.UserCode;
        if (string.IsNullOrEmpty(userCode)) return Array.Empty<Guid>();

        return WithDb(db => (IReadOnlyCollection<Guid>)db.UserCompanyMaps.IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.AppUser!.UserCode == userCode)
            .Select(m => m.TenantEntityId)
            .ToArray());
    }

    private Guid? ResolveActive()
    {
        var accessible = AccessibleCompanyIds;
        if (accessible.Count == 0) return null;

        var header = _accessor.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();
        if (Guid.TryParse(header, out var requested) && accessible.Contains(requested))
            return requested;

        // Fall back to the user's default mapped company, else the sole company.
        var userCode = _currentUser.UserCode;
        if (!string.IsNullOrEmpty(userCode))
        {
            var defaultCompany = WithDb(db => db.UserCompanyMaps.IgnoreQueryFilters()
                .Where(m => !m.IsDeleted && m.AppUser!.UserCode == userCode && m.IsDefault)
                .Select(m => (Guid?)m.TenantEntityId)
                .FirstOrDefault());
            if (defaultCompany.HasValue && accessible.Contains(defaultCompany.Value))
                return defaultCompany;
        }

        return accessible.Count == 1 ? accessible.First() : null;
    }

    /// <summary>
    /// True when the active company is one the user holds an <c>AllSuppliers=true</c> UserCompanyMap for.
    /// Read from a FRESH DbContext scope per request — NEVER a JWT claim — so revoking a grant takes effect
    /// immediately and a stale token can never carry a full-access flag (stale-token privilege escalation).
    /// Admins are already privileged at the seccode layer, so this short-circuits to false for them.
    /// </summary>
    private bool ResolveFullAccess()
    {
        if (_currentUser.IsAdmin) return false;

        var active = ActiveCompanyId;
        if (active is null) return false;

        var userCode = _currentUser.UserCode;
        if (string.IsNullOrEmpty(userCode)) return false;

        return WithDb(db => db.UserCompanyMaps.IgnoreQueryFilters()
            .Any(m => !m.IsDeleted
                      && m.AppUser!.UserCode == userCode
                      && m.TenantEntityId == active
                      && m.AllSuppliers));
    }

    private Dictionary<Guid, Guid> GetShareMap(SharedEndpoint endpoint)
    {
        if (_shareMaps.TryGetValue(endpoint, out var existing)) return existing;

        // IgnoreQueryFilters: this read backs the company filter, so it must not be company-filtered
        // itself. It IS still tenant-relevant — restrict by the current tenant explicitly so a member
        // mapping from another tenant can never leak in.
        var tenantId = _currentUser.TenantId;
        var rows = WithDb(db => db.CompanyShareGroupMembers
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.Endpoint == endpoint)
            .Where(m => m.CompanyShareGroup!.IsEnabled && !m.CompanyShareGroup.IsDeleted)
            .Where(m => tenantId == null || m.TenantId == tenantId)
            .Select(m => new { m.MemberTenantEntityId, m.CompanyShareGroup!.SourceTenantEntityId })
            .ToList());

        var map = new Dictionary<Guid, Guid>();
        foreach (var r in rows)
            map[r.MemberTenantEntityId] = r.SourceTenantEntityId;

        _shareMaps[endpoint] = map;
        return map;
    }
}
