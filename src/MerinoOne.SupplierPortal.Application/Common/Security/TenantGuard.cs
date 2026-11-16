using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;

namespace MerinoOne.SupplierPortal.Application.Common.Security;

/// <summary>
/// Explicit tenant predicates for handlers that read with <c>IgnoreQueryFilters()</c>. That call drops EVERY
/// global filter for the whole query (tenant, company, seccode, soft-delete), so a handler that uses it must
/// re-apply the tenant rule itself or a tenant-A principal can read / act on tenant-B rows by GUID.
/// <para>
/// The rule mirrors <c>AppDbContext.BuildTenantPredicate</c>: the Platform Admin and the system principal
/// bypass; everyone else sees only rows whose <c>TenantId</c> is non-null AND equals their own. A principal
/// with no tenant that does not bypass FAILS CLOSED (sees nothing). Unlike the global filter this is NOT tied to
/// the <c>Scope.FiltersEnabled</c> backfill gate — the same as the explicit sync-log / config guards.
/// </para>
/// </summary>
public static class TenantGuard
{
    /// <summary>Platform Admin + system principal (workers, seeders) are cross-tenant.</summary>
    public static bool BypassesTenant(this ICurrentUser user) => user.IsPlatformAdmin || user is ISystemPrincipal;

    /// <summary>True when <paramref name="entityTenantId"/> is visible to <paramref name="user"/>.</summary>
    public static bool CanAccessTenant(this ICurrentUser user, Guid? entityTenantId) =>
        user.BypassesTenant() || (entityTenantId.HasValue && entityTenantId == user.TenantId);

    /// <summary>Users of the caller's tenant only.</summary>
    public static IQueryable<AppUser> InCallerTenant(this IQueryable<AppUser> q, ICurrentUser user)
    {
        if (user.BypassesTenant()) return q;
        var tid = user.TenantId;
        return q.Where(u => u.TenantId != null && u.TenantId == tid);
    }

    /// <summary>
    /// Roles the caller may SEE and ASSIGN: its own tenant's roles, plus legacy null-tenant built-ins (pre-backfill
    /// rows) — but never the cross-tenant <see cref="RoleNames.PlatformAdmin"/> role, which would be an escalation.
    /// </summary>
    public static IQueryable<Role> ReadableBy(this IQueryable<Role> q, ICurrentUser user)
    {
        if (user.BypassesTenant()) return q;
        var tid = user.TenantId;
        return q.Where(r => (r.TenantId != null && r.TenantId == tid)
                            || (r.TenantId == null && r.Name != RoleNames.PlatformAdmin));
    }

    /// <summary>
    /// Roles the caller may MODIFY (rename, permissions): strictly its own tenant's. A null-tenant row is shared
    /// by every tenant, so only the Platform Admin may edit it.
    /// </summary>
    public static IQueryable<Role> OwnedBy(this IQueryable<Role> q, ICurrentUser user)
    {
        if (user.BypassesTenant()) return q;
        var tid = user.TenantId;
        return q.Where(r => r.TenantId != null && r.TenantId == tid);
    }
}
