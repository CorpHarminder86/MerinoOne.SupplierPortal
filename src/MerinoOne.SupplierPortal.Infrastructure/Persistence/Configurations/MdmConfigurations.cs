using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

// ============================================================================================
// Reference masters (INFOR LN). Tenant-scoped: Currency, Country, State, City, PostalCode —
// key (TenantId, Code). Company-scoped (sharing-aware): Unit, ItemGroup — key (TenantEntityId, Code).
// ApplyBaseEntityConvention maps the scope columns from the ITenantOwned / ICompanyScoped markers.
// ============================================================================================

public class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> b)
    {
        b.ApplyBaseEntityConvention("Currency", "mdm", "currency");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(10).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(100).IsRequired();
        b.Property(x => x.IsoCode).HasColumnName("isoCode").HasMaxLength(3);
        b.Property(x => x.Symbol).HasColumnName("symbol").HasMaxLength(10);
        b.Property(x => x.DecimalPlaces).HasColumnName("decimalPlaces").HasDefaultValue(2);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_Currency_tenant_code").IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.Description }).HasDatabaseName("IX_Currency_tenant_description");
    }
}

public class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> b)
    {
        b.ApplyBaseEntityConvention("Country", "mdm", "country");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(10).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(150).IsRequired();
        b.Property(x => x.IsoCode2).HasColumnName("isoCode2").HasMaxLength(2);
        b.Property(x => x.IsoCode3).HasColumnName("isoCode3").HasMaxLength(3);
        b.Property(x => x.TelephoneCode).HasColumnName("telephoneCode").HasMaxLength(10);
        b.Property(x => x.CurrencyId).HasColumnName("currencyId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId)
            .HasConstraintName("FK_Country_Currency_currencyId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_Country_tenant_code").IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.Description }).HasDatabaseName("IX_Country_tenant_description");
        b.HasIndex(x => x.CurrencyId).HasDatabaseName("IX_Country_currencyId");
    }
}

public class StateConfiguration : IEntityTypeConfiguration<State>
{
    public void Configure(EntityTypeBuilder<State> b)
    {
        b.ApplyBaseEntityConvention("State", "mdm", "state");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(150).IsRequired();
        b.Property(x => x.CountryId).HasColumnName("countryId").HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.IsoCode).HasColumnName("isoCode").HasMaxLength(10);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryId)
            .HasConstraintName("FK_State_Country_countryId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_State_tenant_code").IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.Description }).HasDatabaseName("IX_State_tenant_description");
        b.HasIndex(x => x.CountryId).HasDatabaseName("IX_State_countryId");
    }
}

public class CityConfiguration : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> b)
    {
        b.ApplyBaseEntityConvention("City", "mdm", "city");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(150).IsRequired();
        b.Property(x => x.CountryId).HasColumnName("countryId").HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.StateId).HasColumnName("stateId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryId)
            .HasConstraintName("FK_City_Country_countryId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.State).WithMany().HasForeignKey(x => x.StateId)
            .HasConstraintName("FK_City_State_stateId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_City_tenant_code").IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.Description }).HasDatabaseName("IX_City_tenant_description");
        b.HasIndex(x => x.CountryId).HasDatabaseName("IX_City_countryId");
        b.HasIndex(x => x.StateId).HasDatabaseName("IX_City_stateId");
    }
}

public class PostalCodeConfiguration : IEntityTypeConfiguration<PostalCode>
{
    public void Configure(EntityTypeBuilder<PostalCode> b)
    {
        b.ApplyBaseEntityConvention("PostalCode", "mdm", "postalCode");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Area).HasColumnName("area").HasMaxLength(150);
        b.Property(x => x.CountryId).HasColumnName("countryId").HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.StateId).HasColumnName("stateId").HasColumnType("uniqueidentifier");
        b.Property(x => x.CityId).HasColumnName("cityId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryId)
            .HasConstraintName("FK_PostalCode_Country_countryId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.State).WithMany().HasForeignKey(x => x.StateId)
            .HasConstraintName("FK_PostalCode_State_stateId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.City).WithMany().HasForeignKey(x => x.CityId)
            .HasConstraintName("FK_PostalCode_City_cityId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_PostalCode_tenant_code").IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => x.CountryId).HasDatabaseName("IX_PostalCode_countryId");
        b.HasIndex(x => x.StateId).HasDatabaseName("IX_PostalCode_stateId");
        b.HasIndex(x => x.CityId).HasDatabaseName("IX_PostalCode_cityId");
    }
}

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> b)
    {
        b.ApplyBaseEntityConvention("Unit", "mdm", "unit");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(150).IsRequired();
        b.Property(x => x.UnitType).HasColumnName("unitType").HasConversion<int>().IsRequired();
        b.Property(x => x.IsoCode).HasColumnName("isoCode").HasMaxLength(10);
        b.Property(x => x.DecimalPlaces).HasColumnName("decimalPlaces").HasDefaultValue(2);
        b.Property(x => x.ConversionFactor).HasColumnName("conversionFactor")
            .HasColumnType("decimal(18,6)").HasDefaultValue(1m);
        b.Property(x => x.BaseUnitId).HasColumnName("baseUnitId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasOne(x => x.BaseUnit).WithMany().HasForeignKey(x => x.BaseUnitId)
            .HasConstraintName("FK_Unit_Unit_baseUnitId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_Unit_company_code").IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId }).HasDatabaseName("IX_Unit_tenant_company");
        b.HasIndex(x => x.BaseUnitId).HasDatabaseName("IX_Unit_baseUnitId");
    }
}

public class ItemGroupConfiguration : IEntityTypeConfiguration<ItemGroup>
{
    public void Configure(EntityTypeBuilder<ItemGroup> b)
    {
        b.ApplyBaseEntityConvention("ItemGroup", "inv", "itemGroup");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(200).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_ItemGroup_company_code").IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId }).HasDatabaseName("IX_ItemGroup_tenant_company");
    }
}
