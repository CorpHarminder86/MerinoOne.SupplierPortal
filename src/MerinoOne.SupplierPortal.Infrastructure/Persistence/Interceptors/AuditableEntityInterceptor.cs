using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Two responsibilities:
///   1. Stamp Created*/Updated*/Deleted* audit-block fields on AuditableEntity instances.
///   2. Emit field-level AuditEntry rows (TSD §7.6) into audit.AuditEntry within the same
///      SaveChanges transaction. Short-circuits when actor == "seed" or the entity's CreatedBy
///      == "seed" — keeps seeded data from blowing past the SqlExpress 10GB cap.
/// </summary>
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

        // Pass 1 — stamp audit-block + convert hard deletes to soft deletes.
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
                        // Reset to Unchanged then surgically mark only the soft-delete fields as Modified.
                        // Flipping to Modified directly would mark EVERY column as modified, including the
                        // IDENTITY Seq column → SQL Server rejects "Cannot update identity column".
                        entry.State = EntityState.Unchanged;
                        sd.IsDeleted = true;
                        sd.DeletedBy = actor;
                        sd.DeletedOn = now;
                        entry.Property(nameof(ISoftDelete.IsDeleted)).IsModified = true;
                        entry.Property(nameof(ISoftDelete.DeletedBy)).IsModified = true;
                        entry.Property(nameof(ISoftDelete.DeletedOn)).IsModified = true;
                    }
                    break;
            }
        }

        // Pass 2 — emit field-level AuditEntry rows for non-seed mutations.
        // Must re-snapshot the entries list because we'll be Add()ing AuditEntry rows below.
        if (actor == SeedMarker) return;

        var snapshot = ctx.ChangeTracker.Entries().ToList();
        var auditRows = new List<AuditEntry>();

        foreach (var entry in snapshot)
        {
            // Never recurse — audit rows are themselves never audited.
            if (entry.Entity is AuditEntry) continue;

            // Only emit for entities that themselves carry the audit block.
            if (entry.Entity is not AuditableEntity audited) continue;

            // Skip seeded data (matches the Modified short-circuit above).
            if (audited.CreatedBy == SeedMarker) continue;

            var entityName = entry.Metadata.ClrType.Name;
            var idProp = entry.Property(nameof(BaseEntity.Id));
            if (idProp.CurrentValue is not Guid entityId) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    auditRows.Add(new AuditEntry
                    {
                        EntityName = entityName,
                        EntityId = entityId,
                        Operation = "Insert",
                        FieldName = string.Empty,
                        OldValue = null,
                        NewValue = JsonSerializer.Serialize(new { Id = entityId }),
                        ChangedBy = actor,
                        ChangedOn = now,
                    });
                    break;

                case EntityState.Modified:
                    foreach (var prop in entry.Properties)
                    {
                        if (!prop.IsModified) continue;
                        var oldStr = prop.OriginalValue?.ToString();
                        var newStr = prop.CurrentValue?.ToString();
                        if (oldStr == newStr) continue;

                        auditRows.Add(new AuditEntry
                        {
                            EntityName = entityName,
                            EntityId = entityId,
                            Operation = "Update",
                            FieldName = prop.Metadata.Name,
                            OldValue = oldStr,
                            NewValue = newStr,
                            ChangedBy = actor,
                            ChangedOn = now,
                        });
                    }
                    break;

                case EntityState.Deleted:
                    // Soft-delete path was already converted to Modified in Pass 1 — this only
                    // fires on explicit hard deletes (none currently, kept for completeness).
                    auditRows.Add(new AuditEntry
                    {
                        EntityName = entityName,
                        EntityId = entityId,
                        Operation = "Delete",
                        FieldName = string.Empty,
                        OldValue = null,
                        NewValue = null,
                        ChangedBy = actor,
                        ChangedOn = now,
                    });
                    break;
            }
        }

        if (auditRows.Count > 0)
            ctx.Set<AuditEntry>().AddRange(auditRows);
    }
}
