using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ApplyBaseEntityConvention("AppUser", "admin", "appUser");
        b.Property(x => x.UserCode).HasColumnName("userCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.FullName).HasColumnName("fullName").HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        b.Property(x => x.PasswordHash).HasColumnName("passwordHash").HasMaxLength(500).IsRequired();
        b.Property(x => x.IsInternal).HasColumnName("isInternal").HasDefaultValue(false);
        b.Property(x => x.IsMfaEnabled).HasColumnName("isMfaEnabled").HasDefaultValue(false);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);
        b.Property(x => x.MustChangePassword).HasColumnName("mustChangePassword").HasDefaultValue(false);
        b.Property(x => x.LastLoginAt).HasColumnName("lastLoginAt").HasColumnType("datetime2");

        // tenantId nullable (Platform Admin = null); FK to Tenant; index for the tenant filter.
        b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
            .HasConstraintName("FK_AppUser_Tenant_TenantId").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TenantId).HasDatabaseName("IX_AppUser_tenantId");

        b.HasIndex(x => x.UserCode).HasDatabaseName("UQ_AppUser_userCode").IsUnique();
        // Email stays GLOBALLY unique (1 user = 1 tenant).
        b.HasIndex(x => x.Email).HasDatabaseName("UQ_AppUser_email").IsUnique();
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ApplyBaseEntityConvention("Role", "admin", "role");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
        // Role name is unique PER TENANT now (was global). Filtered so soft-deleted rows don't collide.
        b.HasIndex(x => new { x.TenantId, x.Name })
            .HasDatabaseName("UQ_Role_tenant_name")
            .IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ApplyBaseEntityConvention("UserRole", "admin", "userRole");
        b.Property(x => x.AppUserId).HasColumnName("appUserId");
        b.Property(x => x.RoleId).HasColumnName("roleId");

        b.HasOne(x => x.AppUser).WithMany(u => u.UserRoles).HasForeignKey(x => x.AppUserId)
            .HasConstraintName("FK_UserRole_AppUser_AppUserId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId)
            .HasConstraintName("FK_UserRole_Role_RoleId").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AppUserId, x.RoleId }).HasDatabaseName("UQ_UserRole_user_role").IsUnique();
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ApplyBaseEntityConvention("Permission", "admin", "permission");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(400);
        b.Property(x => x.Module).HasColumnName("module").HasMaxLength(50);
        b.HasIndex(x => x.Code).HasDatabaseName("UQ_Permission_code").IsUnique();
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ApplyBaseEntityConvention("RolePermission", "admin", "rolePermission");
        b.Property(x => x.RoleId).HasColumnName("roleId");
        b.Property(x => x.PermissionId).HasColumnName("permissionId");

        b.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId)
            .HasConstraintName("FK_RolePermission_Role_RoleId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Permission).WithMany(p => p.RolePermissions).HasForeignKey(x => x.PermissionId)
            .HasConstraintName("FK_RolePermission_Permission_PermissionId").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RoleId, x.PermissionId }).HasDatabaseName("UQ_RolePermission_role_permission").IsUnique();
    }
}

public class SeccodeConfiguration : IEntityTypeConfiguration<Seccode>
{
    public void Configure(EntityTypeBuilder<Seccode> b)
    {
        b.ApplyBaseEntityConvention("Seccode", "admin", "seccode");
        b.Property(x => x.SeccodeType).HasColumnName("seccodeType").HasMaxLength(1)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SeccodeType>(v));
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.AppUserId).HasColumnName("appUserId");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.TenantId).HasColumnName("tenantId");
        b.Property(x => x.TenantEntityId).HasColumnName("tenantEntityId");

        b.ToTable(t => t.HasCheckConstraint("CK_Seccode_seccodeType", "[seccodeType] IN ('U','G')"));

        b.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId)
            .HasConstraintName("FK_Seccode_AppUser_AppUserId").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_Seccode_supplierId");
    }
}

public class SecRightConfiguration : IEntityTypeConfiguration<SecRight>
{
    public void Configure(EntityTypeBuilder<SecRight> b)
    {
        b.ApplyBaseEntityConvention("SecRight", "admin", "secRight");
        b.Property(x => x.SeccodeId).HasColumnName("seccodeId");
        b.Property(x => x.UserCode).HasColumnName("userCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.CanRead).HasColumnName("canRead").HasDefaultValue(true);
        b.Property(x => x.CanWrite).HasColumnName("canWrite").HasDefaultValue(false);

        b.HasOne(x => x.Seccode).WithMany(s => s.SecRights).HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_SecRight_Seccode_SeccodeId").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserCode).HasDatabaseName("IX_SecRight_userCode");
        b.HasIndex(x => new { x.SeccodeId, x.UserCode })
            .HasDatabaseName("UX_SecRight_seccodeId_userCode")
            .IsUnique()
            .IncludeProperties(x => new { x.CanRead, x.CanWrite });
    }
}

public class SupplierUserMapConfiguration : IEntityTypeConfiguration<SupplierUserMap>
{
    public void Configure(EntityTypeBuilder<SupplierUserMap> b)
    {
        b.ApplyBaseEntityConvention("SupplierUserMap", "admin", "supplierUserMap");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.AppUserId).HasColumnName("appUserId");
        b.Property(x => x.SecRightId).HasColumnName("secRightId");

        b.HasOne(x => x.AppUser).WithMany(u => u.SupplierMaps).HasForeignKey(x => x.AppUserId)
            .HasConstraintName("FK_SupplierUserMap_AppUser_AppUserId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SecRight).WithMany().HasForeignKey(x => x.SecRightId)
            .HasConstraintName("FK_SupplierUserMap_SecRight_SecRightId").OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SupplierId, x.AppUserId })
            .HasDatabaseName("UQ_SupplierUserMap_supplier_user").IsUnique();
    }
}

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ApplyBaseEntityConvention("Tenant", "admin", "tenant");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);
        b.HasIndex(x => x.Name).HasDatabaseName("UQ_Tenant_name").IsUnique();
    }
}

public class TenantEntityConfiguration : IEntityTypeConfiguration<TenantEntity>
{
    public void Configure(EntityTypeBuilder<TenantEntity> b)
    {
        b.ApplyBaseEntityConvention("TenantEntity", "admin", "tenantEntity");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId)
            .HasConstraintName("FK_TenantEntity_Tenant_TenantId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.Code })
            .HasDatabaseName("UQ_TenantEntity_tenant_code").IsUnique();
    }
}

public class UserCompanyMapConfiguration : IEntityTypeConfiguration<UserCompanyMap>
{
    public void Configure(EntityTypeBuilder<UserCompanyMap> b)
    {
        b.ApplyBaseEntityConvention("UserCompanyMap", "admin", "userCompanyMap");
        b.Property(x => x.AppUserId).HasColumnName("appUserId");
        b.Property(x => x.TenantEntityId).HasColumnName("tenantEntityId");
        b.Property(x => x.IsDefault).HasColumnName("isDefault").HasColumnType("bit").HasDefaultValue(false);
        b.Property(x => x.AllSuppliers).HasColumnName("allSuppliers").HasColumnType("bit").HasDefaultValue(false);

        b.HasOne(x => x.AppUser).WithMany(u => u.CompanyMaps).HasForeignKey(x => x.AppUserId)
            .HasConstraintName("FK_UserCompanyMap_AppUser_AppUserId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.TenantEntity).WithMany().HasForeignKey(x => x.TenantEntityId)
            .HasConstraintName("FK_UserCompanyMap_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.AppUserId, x.TenantEntityId })
            .HasDatabaseName("UQ_UserCompanyMap_user_company").IsUnique();
        b.HasIndex(x => x.AppUserId).HasDatabaseName("IX_UserCompanyMap_appUserId");
    }
}

// R5 (TSD R5 Addendum §4.1 / Component 1) — Company = the customer (buying entity), 1:1 to tenantEntityId.
// Aggregate root: the base convention maps audit + soft-delete + seccodeId + RowVersion + tenant scope columns.
// tenantEntityId IS the §4.1 business "buying entity" column (the inherited ITenantScoped property).
public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ApplyBaseEntityConvention("Company", "admin", "company");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Company_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        // §4.1 — one Company per (tenant, buying entity). tenantId + tenantEntityId are the base scope columns.
        // Filtered on isDeleted = 0 so a soft-deleted row never blocks re-creating the same (tenant, entity).
        b.HasIndex("TenantId", "TenantEntityId")
            .HasDatabaseName("UQ_Company_tenant_entity").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

// R5 (TSD R5 Addendum §4.2 / Component 1) — named, ERP-mappable addresses under a Company. Mirrors
// supplier.SupplierAddress (AuditableEntity), plus mandatory addressName + optional, per-company-unique erpCode.
public class CompanyAddressConfiguration : IEntityTypeConfiguration<CompanyAddress>
{
    public void Configure(EntityTypeBuilder<CompanyAddress> b)
    {
        b.ApplyBaseEntityConvention("CompanyAddress", "admin", "companyAddress");
        b.Property(x => x.CompanyId).HasColumnName("companyId");
        b.Property(x => x.AddressName).HasColumnName("addressName").HasMaxLength(150).IsRequired();
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);
        b.Property(x => x.AddressType).HasColumnName("addressType").HasMaxLength(50).IsRequired();
        b.Property(x => x.AddressLine1).HasColumnName("addressLine1").HasMaxLength(300).IsRequired();
        b.Property(x => x.AddressLine2).HasColumnName("addressLine2").HasMaxLength(300);
        b.Property(x => x.City).HasColumnName("city").HasMaxLength(100).IsRequired();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(100).IsRequired();
        b.Property(x => x.Pincode).HasColumnName("pincode").HasMaxLength(20);
        b.Property(x => x.Country).HasColumnName("country").HasMaxLength(100).HasDefaultValue("India");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.Company).WithMany(c => c.Addresses).HasForeignKey(x => x.CompanyId)
            .HasConstraintName("FK_CompanyAddress_Company_companyId").OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.CompanyId).HasDatabaseName("IX_CompanyAddress_company");

        // §4.2 — deterministic inbound ship-to resolution: erpCode unique per company WHEN PRESENT and not
        // soft-deleted. Filtered so NULL erpCodes never collide and a soft-deleted row never blocks re-use.
        b.HasIndex(x => new { x.CompanyId, x.ErpCode })
            .HasDatabaseName("UQ_CompanyAddress_company_erp").IsUnique()
            .HasFilter("[erpCode] IS NOT NULL AND [isDeleted] = 0");
    }
}
