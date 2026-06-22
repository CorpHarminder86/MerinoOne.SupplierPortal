using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ApplyBaseEntityConvention("Item", "inv", "item");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
        b.Property(x => x.HsnCode).HasColumnName("hsnCode").HasMaxLength(20);
        b.Property(x => x.ItemGroupId).HasColumnName("itemGroupId").HasColumnType("uniqueidentifier");
        b.Property(x => x.UnitId).HasColumnName("unitId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        // R4 (2026-06-22) — Addendum A3: LN-fed control flags. NOT NULL with a named default so existing
        // rows are safe (the migration emits DF_Item_isSerialized / DF_Item_isLotControlled).
        b.Property(x => x.IsSerialized).HasColumnName("isSerialized").HasColumnType("bit").HasDefaultValue(false);
        b.Property(x => x.IsLotControlled).HasColumnName("isLotControlled").HasColumnType("bit").HasDefaultValue(false);

        b.HasOne(x => x.ItemGroup).WithMany().HasForeignKey(x => x.ItemGroupId)
            .HasConstraintName("FK_Item_ItemGroup_itemGroupId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId)
            .HasConstraintName("FK_Item_Unit_unitId").OnDelete(DeleteBehavior.Restrict);

        // Promoted to company-scoped: per-source uniqueness (replaces the old global UQ_Item_code).
        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_Item_company_code").IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId }).HasDatabaseName("IX_Item_tenant_company");
        b.HasIndex(x => x.ItemGroupId).HasDatabaseName("IX_Item_itemGroupId");
        b.HasIndex(x => x.UnitId).HasDatabaseName("IX_Item_unitId");
    }
}

public class DeliveryTermConfiguration : IEntityTypeConfiguration<DeliveryTerm>
{
    public void Configure(EntityTypeBuilder<DeliveryTerm> b)
    {
        b.ApplyBaseEntityConvention("DeliveryTerm", "proc", "deliveryTerm");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(200).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        // Per-company (source) uniqueness, soft-delete-aware. Replaces the old global UQ_DeliveryTerm_code.
        // Sharing means a member company writes under its source, so the key is (sourceTenantEntityId, code).
        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_DeliveryTerm_company_code")
            .IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        // Composite scope index for the always-on tenant + (sharing-aware) company read filter.
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId })
            .HasDatabaseName("IX_DeliveryTerm_tenant_company");
    }
}

public class PaymentTermConfiguration : IEntityTypeConfiguration<PaymentTerm>
{
    public void Configure(EntityTypeBuilder<PaymentTerm> b)
    {
        b.ApplyBaseEntityConvention("PaymentTerm", "proc", "paymentTerm");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(200).IsRequired();
        b.Property(x => x.NetDays).HasColumnName("netDays").ValueGeneratedNever();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        // Per-company (source) uniqueness, soft-delete-aware. Replaces the old global UQ_PaymentTerm_code.
        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_PaymentTerm_company_code")
            .IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        // Composite scope index for the always-on tenant + (sharing-aware) company read filter.
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId })
            .HasDatabaseName("IX_PaymentTerm_tenant_company");
    }
}

public class TaxConfiguration : IEntityTypeConfiguration<Tax>
{
    public void Configure(EntityTypeBuilder<Tax> b)
    {
        b.ApplyBaseEntityConvention("Tax", "proc", "tax");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(200).IsRequired();
        b.Property(x => x.TaxRate).HasColumnName("taxRate").HasColumnType("decimal(9,4)");
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        // Company-shared (sharing-aware), ERP-fed (Q6 FINAL) — mirrors DeliveryTerm/PaymentTerm.
        // Per-company (source) uniqueness, soft-delete-aware.
        b.HasIndex(x => new { x.TenantEntityId, x.Code })
            .HasDatabaseName("UQ_Tax_company_code")
            .IsUnique()
            .HasFilter("[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");
        // Composite scope index for the always-on tenant + (sharing-aware) company read filter.
        b.HasIndex(x => new { x.TenantId, x.TenantEntityId })
            .HasDatabaseName("IX_Tax_tenant_company");
    }
}

public class SupplierInviteConfiguration : IEntityTypeConfiguration<SupplierInvite>
{
    public void Configure(EntityTypeBuilder<SupplierInvite> b)
    {
        b.ApplyBaseEntityConvention("SupplierInvite", "admin", "supplierInvite");
        b.Property(x => x.LegalName).HasColumnName("legalName").HasMaxLength(300).IsRequired();
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        b.Property(x => x.InvitedBy).HasColumnName("invitedBy").HasMaxLength(100).IsRequired();
        b.Property(x => x.MobileNo).HasColumnName("mobileNo").HasMaxLength(20);
        b.Property(x => x.InvitedAt).HasColumnName("invitedAt").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.Token).HasColumnName("token").HasMaxLength(64).IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expiresAt").HasColumnType("datetime2");
        b.Property(x => x.ConsumedAt).HasColumnName("consumedAt").HasColumnType("datetime2");
        b.Property(x => x.SupplierId).HasColumnName("supplierId").HasColumnType("uniqueidentifier");
        // tenantId mapped by ITenantOwned block in ApplyBaseEntityConvention. Map the company column
        // explicitly (nullable; required in the validator) and FK it to TenantEntity.
        b.Property(x => x.TenantEntityId).HasColumnName("tenantEntityId").HasColumnType("uniqueidentifier");

        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId)
            .HasConstraintName("FK_SupplierInvite_Tenant_TenantId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TenantEntity>().WithMany().HasForeignKey(x => x.TenantEntityId)
            .HasConstraintName("FK_SupplierInvite_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.Token).HasDatabaseName("UQ_SupplierInvite_token").IsUnique();
        b.HasIndex(x => x.Email).HasDatabaseName("IX_SupplierInvite_email");
        b.HasIndex(x => x.TenantEntityId).HasDatabaseName("IX_SupplierInvite_tenantEntityId");
    }
}

public class InviteOtpConfiguration : IEntityTypeConfiguration<InviteOtp>
{
    public void Configure(EntityTypeBuilder<InviteOtp> b)
    {
        b.ApplyBaseEntityConvention("InviteOtp", "admin", "inviteOtp");
        b.Property(x => x.SupplierInviteId).HasColumnName("supplierInviteId")
            .HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.CodeHash).HasColumnName("codeHash").HasMaxLength(200).IsRequired();
        b.Property(x => x.IssuedAt).HasColumnName("issuedAt").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.ExpiresAt).HasColumnName("expiresAt").HasColumnType("datetime2");
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.ConsumedAt).HasColumnName("consumedAt").HasColumnType("datetime2");

        b.HasOne(x => x.Invite)
            .WithMany()
            .HasForeignKey(x => x.SupplierInviteId)
            .HasConstraintName("FK_InviteOtp_SupplierInvite_SupplierInviteId")
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.SupplierInviteId, x.IssuedAt })
            .HasDatabaseName("IX_InviteOtp_supplierInviteId_issuedAt")
            .IsDescending(false, true);
    }
}

public class LoginOtpConfiguration : IEntityTypeConfiguration<LoginOtp>
{
    public void Configure(EntityTypeBuilder<LoginOtp> b)
    {
        b.ApplyBaseEntityConvention("LoginOtp", "admin", "loginOtp");
        b.Property(x => x.AppUserId).HasColumnName("appUserId")
            .HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.CodeHash).HasColumnName("codeHash").HasMaxLength(200).IsRequired();
        b.Property(x => x.IssuedAt).HasColumnName("issuedAt").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.ExpiresAt).HasColumnName("expiresAt").HasColumnType("datetime2");
        b.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        b.Property(x => x.ConsumedAt).HasColumnName("consumedAt").HasColumnType("datetime2");
        b.Property(x => x.MfaToken).HasColumnName("mfaToken").HasMaxLength(64).IsRequired();

        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.AppUserId)
            .HasConstraintName("FK_LoginOtp_AppUser_AppUserId")
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.MfaToken)
            .HasDatabaseName("UX_LoginOtp_mfaToken")
            .IsUnique();
        b.HasIndex(x => new { x.AppUserId, x.IssuedAt })
            .HasDatabaseName("IX_LoginOtp_appUserId_issuedAt")
            .IsDescending(false, true);
    }
}

public class EmailTemplateConfiguration : IEntityTypeConfiguration<EmailTemplate>
{
    public void Configure(EntityTypeBuilder<EmailTemplate> b)
    {
        b.ApplyBaseEntityConvention("EmailTemplate", "admin", "emailTemplate");
        b.Property(x => x.TemplateKey).HasColumnName("templateKey").HasMaxLength(50).IsRequired();
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(300).IsRequired();
        b.Property(x => x.HtmlBody).HasColumnName("htmlBody").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(1000);

        // Per-tenant template set. Replaces the old global UX_EmailTemplate_templateKey.
        // Filtered so soft-deleted rows don't collide on re-clone.
        b.HasIndex(x => new { x.TenantId, x.TemplateKey })
            .HasDatabaseName("UX_EmailTemplate_tenant_templateKey")
            .IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> b)
    {
        b.ApplyBaseEntityConvention("EmailOutbox", "admin", "emailOutbox");
        b.Property(x => x.TemplateKey).HasColumnName("templateKey").HasMaxLength(50).IsRequired();
        b.Property(x => x.ToEmail).HasColumnName("toEmail").HasMaxLength(256).IsRequired();
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(300).IsRequired();
        b.Property(x => x.HtmlBody).HasColumnName("htmlBody").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        b.Property(x => x.AttemptCount).HasColumnName("attemptCount").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).HasColumnName("nextAttemptAt").HasColumnType("datetime2").IsRequired();
        b.Property(x => x.SentAt).HasColumnName("sentAt").HasColumnType("datetime2");
        b.Property(x => x.LastError).HasColumnName("lastError").HasMaxLength(2000);

        // Worker poll filter: pick Pending rows whose retry window has elapsed.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("IX_EmailOutbox_status_nextAttemptAt");
    }
}
