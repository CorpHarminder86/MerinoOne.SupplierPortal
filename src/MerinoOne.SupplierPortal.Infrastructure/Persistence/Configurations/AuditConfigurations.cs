using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps audit.AuditEntry per TSD §7.6 — generic single-table audit ledger.
/// Inherits BaseEntity (two-key pattern via ApplyBaseEntityConvention) but NOT AuditableEntity,
/// so the convention extension skips the audit-block branch automatically.
/// </summary>
public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ApplyBaseEntityConvention("AuditEntry", "audit", "auditEntry");

        b.Property(x => x.EntityName)
            .HasColumnName("entityName").HasMaxLength(100).IsRequired();

        b.Property(x => x.EntityId)
            .HasColumnName("entityId").HasColumnType("uniqueidentifier").IsRequired();

        b.Property(x => x.Operation)
            .HasColumnName("operation").HasMaxLength(20).IsRequired();

        b.Property(x => x.FieldName)
            .HasColumnName("fieldName").HasMaxLength(100).IsRequired();

        b.Property(x => x.OldValue)
            .HasColumnName("oldValue").HasColumnType("nvarchar(max)");

        b.Property(x => x.NewValue)
            .HasColumnName("newValue").HasColumnType("nvarchar(max)");

        b.Property(x => x.ChangedBy)
            .HasColumnName("changedBy").HasMaxLength(100).IsRequired();

        b.Property(x => x.ChangedOn)
            .HasColumnName("changedOn").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");

        b.ToTable(t => t.HasCheckConstraint(
            "CK_AuditEntry_operation",
            "[operation] IN ('Insert','Update','Delete')"));

        // Lookup index: entries for a given entity instance, newest first.
        b.HasIndex(x => new { x.EntityName, x.EntityId, x.ChangedOn })
            .HasDatabaseName("IX_AuditEntry_entity")
            .IsDescending(false, false, true);
    }
}
