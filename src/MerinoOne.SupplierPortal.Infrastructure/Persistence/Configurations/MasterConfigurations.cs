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
        b.Property(x => x.Uom).HasColumnName("uom").HasMaxLength(20).IsRequired().HasDefaultValue("EA");
        b.Property(x => x.HsnCode).HasColumnName("hsnCode").HasMaxLength(20);
        b.Property(x => x.IsActive).HasColumnName("isActive").HasDefaultValue(true);

        b.HasIndex(x => x.Code).HasDatabaseName("UQ_Item_code").IsUnique();
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

        b.HasIndex(x => x.Code).HasDatabaseName("UQ_DeliveryTerm_code").IsUnique();
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

        b.HasIndex(x => x.Code).HasDatabaseName("UQ_PaymentTerm_code").IsUnique();
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
        b.Property(x => x.InvitedAt).HasColumnName("invitedAt").HasColumnType("datetime2")
            .HasDefaultValueSql("SYSUTCDATETIME()");
        b.Property(x => x.Token).HasColumnName("token").HasMaxLength(64).IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expiresAt").HasColumnType("datetime2");
        b.Property(x => x.ConsumedAt).HasColumnName("consumedAt").HasColumnType("datetime2");
        b.Property(x => x.SupplierId).HasColumnName("supplierId").HasColumnType("uniqueidentifier");

        b.HasIndex(x => x.Token).HasDatabaseName("UQ_SupplierInvite_token").IsUnique();
        b.HasIndex(x => x.Email).HasDatabaseName("IX_SupplierInvite_email");
    }
}
