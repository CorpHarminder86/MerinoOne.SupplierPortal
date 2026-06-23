using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierVerificationEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;
using SupplierBankDetailEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierBankDetail;
using SupplierLicenseEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierLicense;
using SupplierChangeRequestEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequest;
using SupplierChangeRequestLineEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequestLine;

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

        // R4 (2026-06-22) — Module 1: term/currency FKs + denormalized snapshots, PO-response mode, ERP code.
        b.Property(x => x.CurrencyId).HasColumnName("currencyId").HasColumnType("uniqueidentifier");
        b.Property(x => x.PaymentTermId).HasColumnName("paymentTermId").HasColumnType("uniqueidentifier");
        b.Property(x => x.DeliveryTermId).HasColumnName("deliveryTermId").HasColumnType("uniqueidentifier");
        b.Property(x => x.PaymentTermCode).HasColumnName("paymentTermCode").HasMaxLength(40);
        b.Property(x => x.DeliveryTermCode).HasColumnName("deliveryTermCode").HasMaxLength(40);
        b.Property(x => x.PoResponseMode).HasColumnName("poResponseMode").HasConversion<string>()
            .HasMaxLength(20).HasDefaultValue(PoResponseMode.Manual).IsRequired();
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Supplier_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);
        // Company the supplier belongs to (TenantEntityId carried by BaseAggregateRoot). No nav prop
        // on Supplier so configure the FK against the principal type directly.
        b.HasOne<TenantEntity>().WithMany().HasForeignKey("TenantEntityId")
            .HasConstraintName("FK_Supplier_TenantEntity_TenantEntityId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId)
            .HasConstraintName("FK_Supplier_Currency_CurrencyId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PaymentTerm).WithMany().HasForeignKey(x => x.PaymentTermId)
            .HasConstraintName("FK_Supplier_PaymentTerm_PaymentTermId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DeliveryTerm).WithMany().HasForeignKey(x => x.DeliveryTermId)
            .HasConstraintName("FK_Supplier_DeliveryTerm_DeliveryTermId").OnDelete(DeleteBehavior.Restrict);

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
        // R4 (2026-06-22) — FK lookup indexes for the new term/currency references.
        b.HasIndex(x => x.CurrencyId).HasDatabaseName("IX_Supplier_currencyId");
        b.HasIndex(x => x.PaymentTermId).HasDatabaseName("IX_Supplier_paymentTermId");
        b.HasIndex(x => x.DeliveryTermId).HasDatabaseName("IX_Supplier_deliveryTermId");
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
        b.Property(x => x.Area).HasColumnName("area").HasMaxLength(150);
        b.Property(x => x.City).HasColumnName("city").HasMaxLength(100).IsRequired();
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(100).IsRequired();
        b.Property(x => x.Pincode).HasColumnName("pincode").HasMaxLength(20);
        b.Property(x => x.Country).HasColumnName("country").HasMaxLength(100);

        // Optional geo-master links (tenant-scoped masters). Snapshot strings above remain authoritative
        // for display / international free entry; these resolve when the address was picked via autocomplete.
        b.Property(x => x.CountryId).HasColumnName("countryId").HasColumnType("uniqueidentifier");
        b.Property(x => x.StateId).HasColumnName("stateId").HasColumnType("uniqueidentifier");
        b.Property(x => x.CityId).HasColumnName("cityId").HasColumnType("uniqueidentifier");
        b.Property(x => x.PostalCodeId).HasColumnName("postalCodeId").HasColumnType("uniqueidentifier");

        // R4 (2026-06-22) — Module 1e: ERP handle.
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.Supplier).WithMany(s => s.Addresses).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierAddress_Supplier_SupplierId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.CountryRef).WithMany().HasForeignKey(x => x.CountryId)
            .HasConstraintName("FK_SupplierAddress_Country_countryId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.StateRef).WithMany().HasForeignKey(x => x.StateId)
            .HasConstraintName("FK_SupplierAddress_State_stateId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CityRef).WithMany().HasForeignKey(x => x.CityId)
            .HasConstraintName("FK_SupplierAddress_City_cityId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PostalCodeRef).WithMany().HasForeignKey(x => x.PostalCodeId)
            .HasConstraintName("FK_SupplierAddress_PostalCode_postalCodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.CountryId).HasDatabaseName("IX_SupplierAddress_countryId");
        b.HasIndex(x => x.StateId).HasDatabaseName("IX_SupplierAddress_stateId");
        b.HasIndex(x => x.CityId).HasDatabaseName("IX_SupplierAddress_cityId");
        b.HasIndex(x => x.PostalCodeId).HasDatabaseName("IX_SupplierAddress_postalCodeId");
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

        // R4 (2026-06-22) — Module 1e: ERP handle.
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        // R4 (2026-06-23) — optional link to one of the supplier's addresses. Logical semantics are SetNull
        // (nulling addressId when its address goes away), but the DB FK is NoAction: SupplierContact and
        // SupplierAddress BOTH already cascade-delete from Supplier, so a DB-level SetNull here introduces a
        // multiple-cascade-path / cycle (SQL error 1785). The portal is soft-delete only (rows are never
        // physically deleted) so no DB cascade ever fires anyway — the app nulls addressId on address removal.
        b.Property(x => x.AddressId).HasColumnName("addressId").HasColumnType("uniqueidentifier");

        b.HasOne(x => x.Supplier).WithMany(s => s.Contacts).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierContact_Supplier_SupplierId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Address).WithMany().HasForeignKey(x => x.AddressId)
            .HasConstraintName("FK_SupplierContact_SupplierAddress_addressId").OnDelete(DeleteBehavior.NoAction);

        b.HasIndex(x => new { x.SupplierId, x.Email })
            .HasDatabaseName("UQ_SupplierContact_supplier_email").IsUnique();
        b.HasIndex(x => x.AddressId).HasDatabaseName("IX_SupplierContact_addressId");
    }
}

public class SupplierBankDetailConfiguration : IEntityTypeConfiguration<SupplierBankDetailEntity>
{
    public void Configure(EntityTypeBuilder<SupplierBankDetailEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierBankDetail", "supplier", "supplierBankDetail");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.BankName).HasColumnName("bankName").HasMaxLength(200).IsRequired();
        b.Property(x => x.BankAddress).HasColumnName("bankAddress").HasMaxLength(500).IsRequired();
        b.Property(x => x.AccountName).HasColumnName("accountName").HasMaxLength(200).IsRequired();
        b.Property(x => x.AccountNumber).HasColumnName("accountNumber").HasMaxLength(64).IsRequired();
        b.Property(x => x.CurrencyId).HasColumnName("currencyId").HasColumnType("uniqueidentifier").IsRequired();
        b.Property(x => x.IfscCode).HasColumnName("ifscCode").HasMaxLength(20).IsRequired();
        b.Property(x => x.SwiftCode).HasColumnName("swiftCode").HasMaxLength(20);
        b.Property(x => x.IsPrimary).HasColumnName("isPrimary").HasDefaultValue(false);
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.Supplier).WithMany(s => s.BankDetails).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierBankDetail_Supplier_SupplierId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId)
            .HasConstraintName("FK_SupplierBankDetail_Currency_CurrencyId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_SupplierBankDetail_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_SupplierBankDetail_supplierId");
        b.HasIndex(x => x.CurrencyId).HasDatabaseName("IX_SupplierBankDetail_currencyId");
        b.HasIndex(x => new { x.SupplierId, x.AccountNumber })
            .HasDatabaseName("UQ_SupplierBankDetail_supplier_account").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class SupplierLicenseConfiguration : IEntityTypeConfiguration<SupplierLicenseEntity>
{
    public void Configure(EntityTypeBuilder<SupplierLicenseEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierLicense", "supplier", "supplierLicense");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.LicenseNumber).HasColumnName("licenseNumber").HasMaxLength(100).IsRequired();
        b.Property(x => x.LicenseType).HasColumnName("licenseType").HasMaxLength(100).IsRequired();
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(1000);
        b.Property(x => x.IssueDate).HasColumnName("issueDate").HasColumnType("date");
        b.Property(x => x.ExpiryDate).HasColumnName("expiryDate").HasColumnType("date");
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.Supplier).WithMany(s => s.Licenses).HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierLicense_Supplier_SupplierId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_SupplierLicense_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_SupplierLicense_supplierId");
        b.HasIndex(x => x.ExpiryDate).HasDatabaseName("IX_SupplierLicense_expiry")
            .HasFilter("[isDeleted] = 0");
    }
}

// R4 (2026-06-22) — Module 2 (Supplier Change Management).

public class SupplierChangeRequestConfiguration : IEntityTypeConfiguration<SupplierChangeRequestEntity>
{
    public void Configure(EntityTypeBuilder<SupplierChangeRequestEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierChangeRequest", "supplier", "supplierChangeRequest");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.ChangeStatus).HasColumnName("changeStatus").HasConversion<string>()
            .HasMaxLength(20).HasDefaultValue(ChangeRequestStatus.Draft).IsRequired();
        b.Property(x => x.RequestedBy).HasColumnName("requestedBy").HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestedAt).HasColumnName("requestedAt").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.ReviewedBy).HasColumnName("reviewedBy").HasMaxLength(100);
        b.Property(x => x.ReviewedAt).HasColumnName("reviewedAt").HasColumnType("datetime2");
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);
        b.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(500);

        b.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_SupplierChangeRequest_Supplier_SupplierId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_SupplierChangeRequest_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.SupplierId, x.ChangeStatus })
            .HasDatabaseName("IX_SupplierChangeRequest_supplier_status");
    }
}

public class SupplierChangeRequestLineConfiguration : IEntityTypeConfiguration<SupplierChangeRequestLineEntity>
{
    public void Configure(EntityTypeBuilder<SupplierChangeRequestLineEntity> b)
    {
        b.ApplyBaseEntityConvention("SupplierChangeRequestLine", "supplier", "supplierChangeRequestLine");
        b.Property(x => x.SupplierChangeRequestId).HasColumnName("supplierChangeRequestId");
        b.Property(x => x.TargetEntity).HasColumnName("targetEntity").HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.TargetEntityId).HasColumnName("targetEntityId").HasColumnType("uniqueidentifier");
        b.Property(x => x.Operation).HasColumnName("operation").HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(x => x.FieldName).HasColumnName("fieldName").HasMaxLength(100);
        b.Property(x => x.OldValue).HasColumnName("oldValue").HasMaxLength(1000);
        b.Property(x => x.NewValue).HasColumnName("newValue").HasMaxLength(1000);
        b.Property(x => x.PayloadJson).HasColumnName("payloadJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.PushStatus).HasColumnName("pushStatus").HasConversion<string>()
            .HasMaxLength(20).HasDefaultValue(LinePushStatus.Pending).IsRequired();
        b.Property(x => x.PushedAt).HasColumnName("pushedAt").HasColumnType("datetime2");
        b.Property(x => x.ErpRef).HasColumnName("erpRef").HasMaxLength(100);

        b.HasOne(x => x.SupplierChangeRequest).WithMany(r => r.Lines).HasForeignKey(x => x.SupplierChangeRequestId)
            .HasConstraintName("FK_SupplierChangeRequestLine_SupplierChangeRequest_SupplierChangeRequestId")
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.SupplierChangeRequestId).HasDatabaseName("IX_SupplierChangeRequestLine_request");
    }
}
