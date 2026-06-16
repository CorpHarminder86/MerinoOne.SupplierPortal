using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierVerificationEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class SupplierConfiguration : IEntityTypeConfiguration<SupplierEntity>
{
    public void Configure(EntityTypeBuilder<SupplierEntity> b)
    {
        b.ApplyBaseEntityConvention("Supplier", "supplier", "supplier");

        b.Property(x => x.SupplierCode).HasColumnName("supplierCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.LegalName).HasColumnName("legalName").HasMaxLength(300).IsRequired();
        b.Property(x => x.TradeName).HasColumnName("tradeName").HasMaxLength(300);
        b.Property(x => x.SupplierType).HasColumnName("supplierType").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.GstNumber).HasColumnName("gstNumber").HasMaxLength(20);
        b.Property(x => x.PanNumber).HasColumnName("panNumber").HasMaxLength(20);
        b.Property(x => x.MsmeRegNumber).HasColumnName("msmeRegNumber").HasMaxLength(50);
        b.Property(x => x.MsmeCategory).HasColumnName("msmeCategory").HasMaxLength(50);
        b.Property(x => x.GstValidated).HasColumnName("gstValidated").HasDefaultValue(false);
        b.Property(x => x.PanValidated).HasColumnName("panValidated").HasDefaultValue(false);
        b.Property(x => x.MsmeValidated).HasColumnName("msmeValidated").HasDefaultValue(false);
        b.Property(x => x.RegistrationStatus).HasColumnName("registrationStatus").HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.InvitedBy).HasColumnName("invitedBy").HasMaxLength(100);
        b.Property(x => x.InvitedAt).HasColumnName("invitedAt").HasColumnType("datetime2");
        b.Property(x => x.ApprovedBy).HasColumnName("approvedBy").HasMaxLength(100);
        b.Property(x => x.ApprovedAt).HasColumnName("approvedAt").HasColumnType("datetime2");
        b.Property(x => x.ApprovalOverrideComment).HasColumnName("approvalOverrideComment").HasMaxLength(1000);
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);
        b.Property(x => x.Website).HasColumnName("website").HasMaxLength(300);
        b.Property(x => x.IsActiveSupplier).HasColumnName("isActiveSupplier").HasDefaultValue(false);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Supplier_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);
        // Company the supplier belongs to (TenantEntityId carried by BaseAggregateRoot). No nav prop
        // on Supplier so configure the FK against the principal type directly.
        b.HasOne<TenantEntity>().WithMany().HasForeignKey("TenantEntityId")
            .HasConstraintName("FK_Supplier_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);

        // supplierCode + legalName are PER-TENANT unique now (was global). Filtered (soft-delete-aware).
        b.HasIndex("TenantId", "LegalName")
            .HasDatabaseName("UQ_Supplier_tenant_legalName").IsUnique()
            .HasFilter("[isDeleted] = 0");
        b.HasIndex("TenantId", "SupplierCode")
            .HasDatabaseName("UQ_Supplier_tenant_supplierCode").IsUnique()
            .HasFilter("[isDeleted] = 0");
        b.HasIndex(x => x.RegistrationStatus).HasDatabaseName("IX_Supplier_registrationStatus");
        // Composite scope index for the tenant + company business-data filter on the hot supplier path.
        b.HasIndex("TenantId", "TenantEntityId")
            .HasDatabaseName("IX_Supplier_tenant_company");
    }
}

public class SupplierVerificationConfiguration : IEntityTypeConfiguration<SupplierVerificationEntity>
{
    public void Configure(EntityTypeBuilder<SupplierVerificationEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierVerification", "supplier", "supplierVerification");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.VerificationType).HasColumnName("verificationType").HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.AttemptedAt).HasColumnName("attemptedAt").HasColumnType("datetime2");
        b.Property(x => x.AttemptedBy).HasColumnName("attemptedBy").HasMaxLength(100);
        b.Property(x => x.ProviderName).HasColumnName("providerName").HasMaxLength(100);
        b.Property(x => x.Result).HasColumnName("result").HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.ResponsePayload).HasColumnName("responsePayload").HasColumnType("nvarchar(max)");
        b.Property(x => x.Comments).HasColumnName("comments").HasMaxLength(1000);

        b.ToTable(t => t.HasCheckConstraint("CK_SupplierVerification_verificationType", "[verificationType] IN ('GST','PAN','MSME')"));
        b.ToTable(t => t.HasCheckConstraint("CK_SupplierVerification_result", "[result] IN ('Pass','Fail','Error')"));

        b.HasOne(x => x.Supplier).WithMany(s => s.Verifications).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierVerification_Supplier_SupplierId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_SupplierVerification_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.SupplierId, x.VerificationType, x.AttemptedAt })
            .HasDatabaseName("IX_SupplierVerification_supplier_type")
            .IsDescending(false, false, true);
    }
}

public class SupplierAddressConfiguration : IEntityTypeConfiguration<SupplierAddressEntity>
{
    public void Configure(EntityTypeBuilder<SupplierAddressEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierAddress", "supplier", "supplierAddress");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.AddressType).HasColumnName("addressType").HasMaxLength(50).IsRequired();
        b.Property(x => x.AddressLine1).HasColumnName("addressLine1").HasMaxLength(300).IsRequired();
        b.Property(x => x.AddressLine2).HasColumnName("addressLine2").HasMaxLength(300);
        b.Property(x => x.City).HasColumnName("city").HasMaxLength(100).IsRequired();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(100).IsRequired();
        b.Property(x => x.Pincode).HasColumnName("pincode").HasMaxLength(20);
        b.Property(x => x.Country).HasColumnName("country").HasMaxLength(100);

        b.HasOne(x => x.Supplier).WithMany(s => s.Addresses).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierAddress_Supplier_SupplierId").OnDelete(DeleteBehavior.Cascade);
    }
}

public class SupplierContactConfiguration : IEntityTypeConfiguration<SupplierContactEntity>
{
    public void Configure(EntityTypeBuilder<SupplierContactEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierContact", "supplier", "supplierContact");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.ContactName).HasColumnName("contactName").HasMaxLength(200).IsRequired();
        b.Property(x => x.Designation).HasColumnName("designation").HasMaxLength(100);
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        b.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(20);
        b.Property(x => x.IsPrimary).HasColumnName("isPrimary").HasDefaultValue(false);

        b.HasOne(x => x.Supplier).WithMany(s => s.Contacts).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierContact_Supplier_SupplierId").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SupplierId, x.Email })
            .HasDatabaseName("UQ_SupplierContact_supplier_email").IsUnique();
    }
}
