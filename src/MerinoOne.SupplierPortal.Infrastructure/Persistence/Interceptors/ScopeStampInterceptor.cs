using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps the always-on scope columns on inserted rows so no row is ever invisible because a handler
/// forgot to set its scope:
///   - <c>TenantId</c> from <see cref="ICurrentUser.TenantId"/> on every ITenantOwned / ITenantScoped /
///     ICompanyScoped entity (when unset).
///   - <c>TenantEntityId</c> from <see cref="ICurrentCompany.ActiveCompanyId"/> on company-scoped types
///     (ICompanyScoped + ITenantScoped aggregates) when unset.
///
/// Sibling of <see cref="AuditableEntityInterceptor"/>, registered in <c>AppDbContext.OnConfiguring</c>.
/// Skips entirely under a system principal (<see cref="ISystemPrincipal"/>) — seeders, workers, migrations
/// and outbound integration set scope explicitly and must not have the active-request scope imposed on them.
/// </summary>
public class ScopeStampInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentCompany _currentCompany;

    public ScopeStampInterceptor(ICurrentUser currentUser, ICurrentCompany currentCompany)
    {
        _currentUser = currentUser;
        _currentCompany = currentCompany;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? ctx)
    {
        if (ctx is null) return;

        // System principals (seeders/workers/migrations/outbound) stamp scope explicitly — never override.
        if (_currentUser is ISystemPrincipal) return;

        // Capture once; ActiveCompanyId may lazily issue a query, so don't re-read it per entity.
        var tenantId = _currentUser.TenantId;
        var activeCompanyId = _currentCompany.ActiveCompanyId;

        foreach (var entry in ctx.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;

            switch (entry.Entity)
            {
                // Tenant + company-scoped business data: stamp both when unset.
                case ICompanyScoped company:
                    if (company.TenantId is null && tenantId.HasValue) company.TenantId = tenantId;
                    if (company.TenantEntityId is null && activeCompanyId.HasValue) company.TenantEntityId = activeCompanyId;
                    break;

                case ITenantScoped scoped:
                    if (scoped.TenantId is null && tenantId.HasValue) scoped.TenantId = tenantId;
                    if (scoped.TenantEntityId is null && activeCompanyId.HasValue) scoped.TenantEntityId = activeCompanyId;
                    break;

                // Tenant-scoped config / integration (no company): stamp tenant only.
                case ITenantOwned owned:
                    if (owned.TenantId is null && tenantId.HasValue) owned.TenantId = tenantId;
                    break;
            }
        }
    }
}
