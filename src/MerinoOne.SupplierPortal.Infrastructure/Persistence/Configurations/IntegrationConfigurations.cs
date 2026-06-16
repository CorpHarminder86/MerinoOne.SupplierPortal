using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class CompanyShareGroupConfiguration : IEntityTypeConfiguration<CompanyShareGroup>
{
    public void Configure(EntityTypeBuilder<CompanyShareGroup> b)
    {
        b.ApplyBaseEntityConvention("CompanyShareGroup", "integration", "companyShareGroup");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.Endpoint).HasColumnName("endpoint").HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceTenantEntityId).HasColumnName("sourceTenantEntityId");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        b.Property(x => x.IsEnabled).HasColumnName("isEnabled").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId)
            .HasConstraintName("FK_CompanyShareGroup_Tenant_TenantId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SourceTenantEntity).WithMany().HasForeignKey(x => x.SourceTenantEntityId)
            .HasConstraintName("FK_CompanyShareGroup_TenantEntity_SourceTenantEntityId").OnDelete(DeleteBehavior.Restrict);

        // Each (tenant, endpoint, source) combination is unique.
        b.HasIndex(x => new { x.TenantId, x.Endpoint, x.SourceTenantEntityId })
            .HasDatabaseName("UQ_CompanyShareGroup_tenant_endpoint_source").IsUnique();
    }
}

public class CompanyShareGroupMemberConfiguration : IEntityTypeConfiguration<CompanyShareGroupMember>
{
    public void Configure(EntityTypeBuilder<CompanyShareGroupMember> b)
    {
        b.ApplyBaseEntityConvention("CompanyShareGroupMember", "integration", "companyShareGroupMember");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.CompanyShareGroupId).HasColumnName("companyShareGroupId");
        b.Property(x => x.MemberTenantEntityId).HasColumnName("memberTenantEntityId");
        b.Property(x => x.Endpoint).HasColumnName("endpoint").HasConversion<string>().HasMaxLength(30).IsRequired();

        b.HasOne(x => x.CompanyShareGroup).WithMany(g => g.Members).HasForeignKey(x => x.CompanyShareGroupId)
            .HasConstraintName("FK_CompanyShareGroupMember_CompanyShareGroup_CompanyShareGroupId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.MemberTenantEntity).WithMany().HasForeignKey(x => x.MemberTenantEntityId)
            .HasConstraintName("FK_CompanyShareGroupMember_TenantEntity_MemberTenantEntityId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.CompanyShareGroupId, x.MemberTenantEntityId })
            .HasDatabaseName("UQ_CompanyShareGroupMember_group_member").IsUnique();
        // A company belongs to at most ONE group per endpoint → ResolveSource is a total function.
        // Filtered so soft-deleted memberships don't block re-adding a company to a group.
        b.HasIndex(x => new { x.Endpoint, x.MemberTenantEntityId })
            .HasDatabaseName("UQ_CompanyShareGroupMember_endpoint_member").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ApplyBaseEntityConvention("ApiKey", "integration", "apiKey");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(200).IsRequired();
        b.Property(x => x.KeyPrefix).HasColumnName("keyPrefix").HasMaxLength(20).IsRequired();
        b.Property(x => x.KeyHash).HasColumnName("keyHash").HasColumnType("char(64)").IsRequired();
        b.Property(x => x.Scopes).HasColumnName("scopes").HasMaxLength(400).IsRequired();
        b.Property(x => x.TenantEntityId).HasColumnName("tenantEntityId").HasColumnType("uniqueidentifier");
        b.Property(x => x.ExpiresAt).HasColumnName("expiresAt").HasColumnType("datetime2");
        b.Property(x => x.LastUsedAt).HasColumnName("lastUsedAt").HasColumnType("datetime2");
        b.Property(x => x.RevokedAt).HasColumnName("revokedAt").HasColumnType("datetime2");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);
        b.Property(x => x.ReplacedByApiKeyId).HasColumnName("replacedByApiKeyId").HasColumnType("uniqueidentifier");

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId)
            .HasConstraintName("FK_ApiKey_Tenant_TenantId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TenantEntity).WithMany().HasForeignKey(x => x.TenantEntityId)
            .HasConstraintName("FK_ApiKey_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);

        // Prefix lookup must be unique for the constant-time hash compare to be a single-row probe.
        b.HasIndex(x => x.KeyPrefix).HasDatabaseName("UX_ApiKey_keyPrefix").IsUnique();
        // Filtered index over live keys for the authentication-handler hot path.
        b.HasIndex(x => x.IsActive).HasDatabaseName("IX_ApiKey_isActive").HasFilter("[isActive] = 1");
    }
}
