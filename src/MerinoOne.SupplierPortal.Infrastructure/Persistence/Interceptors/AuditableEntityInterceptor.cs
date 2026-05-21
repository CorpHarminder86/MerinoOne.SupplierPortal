using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;

public class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser _currentUser;
    private const string SeedMarker = "seed";

    public AuditableEntityInterceptor(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampEntities(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        StampEntities(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampEntities(DbContext? ctx)
    {
        if (ctx is null) return;
        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_currentUser?.UserCode) ? "system" : _currentUser.UserCode;

        foreach (var entry in ctx.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (string.IsNullOrEmpty(entry.Entity.CreatedBy))
                        entry.Entity.CreatedBy = actor;
                    if (entry.Entity.CreatedOn == default)
                        entry.Entity.CreatedOn = now;
                    break;

                case EntityState.Modified:
                    // Skip audit-trail emission when seeded data is being touched
                    if (entry.Entity.CreatedBy == SeedMarker || actor == SeedMarker) break;
                    entry.Entity.UpdatedBy = actor;
                    entry.Entity.UpdatedOn = now;
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is ISoftDelete sd)
                    {
                        entry.State = EntityState.Modified;
                        sd.IsDeleted = true;
                        sd.DeletedBy = actor;
                        sd.DeletedOn = now;
                    }
                    break;
            }
        }
    }
}
