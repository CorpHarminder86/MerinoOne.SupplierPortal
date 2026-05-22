using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ApplyBaseEntityConvention("SystemSetting", "settings", "systemSetting");

        b.Property(x => x.Category).HasColumnName("category").HasMaxLength(50).IsRequired();
        b.Property(x => x.SettingKey).HasColumnName("settingKey").HasMaxLength(100).IsRequired();
        b.Property(x => x.SettingValue).HasColumnName("settingValue").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        // Plain unique on (category, settingKey). Filtered uniques aren't well-supported in EF;
        // re-inserting a soft-deleted (category,key) pair would collide. Seed uses NOT EXISTS
        // ignoring isDeleted to keep it idempotent and avoid resurrecting tombstoned rows.
        b.HasIndex(x => new { x.Category, x.SettingKey })
            .HasDatabaseName("UQ_SystemSetting_category_settingKey")
            .IsUnique();

        b.HasIndex(x => x.Category)
            .HasDatabaseName("IX_SystemSetting_category");
    }
}
