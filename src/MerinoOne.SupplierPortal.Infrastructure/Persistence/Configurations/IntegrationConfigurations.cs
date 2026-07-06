using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ApplyBaseEntityConvention("OutboxMessage", "integration", "outboxMessage");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.TransactionType).HasColumnName("transactionType").HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityName).HasColumnName("entityName").HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityId).HasColumnName("entityId").HasColumnType("uniqueidentifier");
        b.Property(x => x.DeterministicKey).HasColumnName("deterministicKey").HasMaxLength(200).IsRequired();
        b.Property(x => x.PayloadJson).HasColumnName("payloadJson").HasColumnType("nvarchar(max)");
        // Status is a string enum with NO CHECK constraint (the dominant convention; the C# enum is the guard).
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(OutboxStatus.Pending).IsRequired();
        b.Property(x => x.AttemptCount).HasColumnName("attemptCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.DispatchedAt).HasColumnName("dispatchedAt").HasColumnType("datetime2");
        b.Property(x => x.AckedAt).HasColumnName("ackedAt").HasColumnType("datetime2");
        b.Property(x => x.LastError).HasColumnName("lastError").HasMaxLength(2000);
        // R9 (migration 0047): eligibility-gate bookkeeping + the Phase-C sequence hook (FK deferred to Phase C).
        b.Property(x => x.GateVersion).HasColumnName("gateVersion").HasColumnType("int");
        b.Property(x => x.SkipReason).HasColumnName("skipReason").HasMaxLength(500);
        b.Property(x => x.SequenceInstanceStepId).HasColumnName("sequenceInstanceStepId").HasColumnType("uniqueidentifier");
        // R9 (migration 0048): code-owned failure class (D-R9-5) — Permanent (4xx) | Retriable (5xx/timeout).
        b.Property(x => x.ErrorClass).HasColumnName("errorClass").HasMaxLength(10);
        b.ToTable(t => t.HasCheckConstraint("CK_OutboxMessage_errorClass",
            "[errorClass] IS NULL OR [errorClass] IN ('Permanent','Retriable')"));
        // rowVersion (IHasRowVersion) mapped centrally by the AppDbContext convention → .IsRowVersion(),
        // column "rowVersion". Backs the dispatcher's per-row Pending→Sending claim (review B1, migration 0023).

        // Enqueue idempotency (review B2, migration 0023): the deterministic key is only unique per
        // tenant+supplier, so the uniqueness MUST be tenant-qualified. A composite (tenantId, deterministicKey)
        // filtered-unique index — at most one live row per (tenant, key) — replaces the old global
        // single-column UQ_OutboxMessage_deterministicKey that could collapse two tenants' invoices.
        b.HasIndex(x => new { x.TenantId, x.DeterministicKey })
            .HasDatabaseName("UQ_OutboxMessage_tenant_deterministicKey").IsUnique()
            .HasFilter("[isDeleted] = 0");
        // Dispatcher scan: pick live rows by status.
        b.HasIndex(x => x.Status)
            .HasDatabaseName("IX_OutboxMessage_status")
            .HasFilter("[isDeleted] = 0");
    }
}

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
        // Scopes is an unbounded CSV of scope tokens that grows with every new inbound endpoint; it is never
        // indexed/filtered, so a fixed length just breaks again later (see 0027). nvarchar(max) per the
        // OutboxMessage.payloadJson / InforConnectionSetting precedent in this same file.
        b.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("nvarchar(max)").IsRequired();
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

public class ApiKeyCompanyConfiguration : IEntityTypeConfiguration<ApiKeyCompany>
{
    public void Configure(EntityTypeBuilder<ApiKeyCompany> b)
    {
        b.ApplyBaseEntityConvention("ApiKeyCompany", "integration", "apiKeyCompany");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.ApiKeyId).HasColumnName("apiKeyId");
        b.Property(x => x.TenantEntityId).HasColumnName("tenantEntityId");

        b.HasOne(x => x.ApiKey).WithMany(k => k.Companies).HasForeignKey(x => x.ApiKeyId)
            .HasConstraintName("FK_ApiKeyCompany_ApiKey_ApiKeyId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.TenantEntity).WithMany().HasForeignKey(x => x.TenantEntityId)
            .HasConstraintName("FK_ApiKeyCompany_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);

        // A key binds a given company at most once.
        b.HasIndex(x => new { x.ApiKeyId, x.TenantEntityId })
            .HasDatabaseName("UQ_ApiKeyCompany_apiKey_company").IsUnique();
    }
}

public class InforConnectionSettingConfiguration : IEntityTypeConfiguration<InforConnectionSetting>
{
    public void Configure(EntityTypeBuilder<InforConnectionSetting> b)
    {
        b.ApplyBaseEntityConvention("InforConnectionSetting", "integration", "inforConnectionSetting");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.AccessTokenUrl).HasColumnName("accessTokenUrl").HasMaxLength(500).IsRequired();
        b.Property(x => x.ClientId).HasColumnName("clientId").HasMaxLength(500).IsRequired();
        // Encrypted via ISettingProtector — ciphertext is comfortably under 4000 but use nvarchar(max) to be safe.
        b.Property(x => x.ClientSecret).HasColumnName("clientSecret").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(500).IsRequired();
        b.Property(x => x.Password).HasColumnName("password").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.ApiBaseUrl).HasColumnName("apiBaseUrl").HasMaxLength(500).IsRequired();
        b.Property(x => x.IonC4wsBaseUrl).HasColumnName("ionC4wsBaseUrl").HasMaxLength(500);
        b.Property(x => x.Company).HasColumnName("company").HasMaxLength(200);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        // Exactly one live connection-config row per tenant. Filtered so a soft-deleted row never
        // blocks re-creating the config (mirrors the CompanyShareGroupMember filtered-unique pattern).
        b.HasIndex(x => x.TenantId)
            .HasDatabaseName("UQ_InforConnectionSetting_tenantId")
            .IsUnique()
            .HasFilter("[tenantId] IS NOT NULL AND [isDeleted] = 0");
    }
}
