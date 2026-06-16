using MerinoOne.SupplierPortal.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public static class BaseEntityConfigExtensions
{
    /// <summary>
    /// Applies the two-key pattern per sql-naming-conventions v1.1:
    ///   <entity>Id  UNIQUEIDENTIFIER  PRIMARY KEY NONCLUSTERED  DEFAULT NEWID()
    ///   <entity>Seq INT IDENTITY(1,1) — clustered unique index UX_<Table>_<entity>Seq
    /// Plus the audit block + soft-delete + rowversion when the entity implements those markers.
    /// </summary>
    public static EntityTypeBuilder<T> ApplyBaseEntityConvention<T>(
        this EntityTypeBuilder<T> b,
        string table,
        string schema,
        string camelName)
        where T : BaseEntity
    {
        b.ToTable(table, schema);

        var idCol = camelName + "Id";
        var seqCol = camelName + "Seq";
        var pkName = "PK_" + table;
        var uxSeqName = "UX_" + table + "_" + seqCol;
        var dfIdName = "DF_" + table + "_" + idCol;

        b.HasKey(x => x.Id).HasName(pkName).IsClustered(false);
        b.Property(x => x.Id)
            .HasColumnName(idCol)
            .HasColumnType("uniqueidentifier")
            .HasDefaultValueSql("NEWID()");

        var seqProp = b.Property(x => x.Seq)
            .HasColumnName(seqCol)
            .ValueGeneratedOnAdd();
        // SQL Server IDENTITY column — must not appear in UPDATE statements.
        // The audit interceptor flips Deleted→Modified for soft-delete which otherwise
        // marks every column as modified including this identity column.
        seqProp.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        b.HasIndex(x => x.Seq)
            .HasDatabaseName(uxSeqName)
            .IsUnique()
            .IsClustered();

        if (typeof(AuditableEntity).IsAssignableFrom(typeof(T)))
        {
            b.Property("CreatedOn").HasColumnName("createdOn").HasColumnType("datetime2")
                .HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property("CreatedBy").HasColumnName("createdBy").HasMaxLength(100).IsRequired();
            b.Property("UpdatedOn").HasColumnName("updatedOn").HasColumnType("datetime2");
            b.Property("UpdatedBy").HasColumnName("updatedBy").HasMaxLength(100);
            b.Property("IsDeleted").HasColumnName("isDeleted").HasColumnType("bit")
                .HasDefaultValue(false);
            b.Property("DeletedOn").HasColumnName("deletedOn").HasColumnType("datetime2");
            b.Property("DeletedBy").HasColumnName("deletedBy").HasMaxLength(100);
        }

        if (typeof(BaseAggregateRoot).IsAssignableFrom(typeof(T)))
        {
            b.Property("SeccodeId").HasColumnName("seccodeId").HasColumnType("uniqueidentifier").IsRequired();
            // TenantId/TenantEntityId mapped by the ITenantScoped/ICompanyScoped/ITenantOwned blocks below.
            // rowVersion configured globally via ApplyGlobalFilters → IHasRowVersion branch
        }

        // Scope columns (Phase 1: nullable; tightened post-backfill). Mapped centrally so every
        // tenant-/company-scoped entity gets identical column names + types. ITenantScoped and
        // ICompanyScoped both carry tenantId + tenantEntityId; ITenantOwned carries tenantId only.
        var tenantIdMapped = false;
        if (typeof(ITenantScoped).IsAssignableFrom(typeof(T)) || typeof(ICompanyScoped).IsAssignableFrom(typeof(T)))
        {
            b.Property("TenantId").HasColumnName("tenantId").HasColumnType("uniqueidentifier");
            b.Property("TenantEntityId").HasColumnName("tenantEntityId").HasColumnType("uniqueidentifier");
            tenantIdMapped = true;
        }
        if (!tenantIdMapped && typeof(ITenantOwned).IsAssignableFrom(typeof(T)))
        {
            b.Property("TenantId").HasColumnName("tenantId").HasColumnType("uniqueidentifier");
        }

        return b;
    }
}
