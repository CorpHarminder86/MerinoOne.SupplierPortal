using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using Microsoft.EntityFrameworkCore;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierVerificationEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface IAppDbContext
{
    DbSet<AppUser> AppUsers { get; }
    DbSet<Role> Roles { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<Permission> Permissions { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<Seccode> Seccodes { get; }
    DbSet<SecRight> SecRights { get; }
    DbSet<SupplierUserMap> SupplierUserMaps { get; }
    DbSet<SupplierInvite> SupplierInvites { get; }
    DbSet<InviteOtp> InviteOtps { get; }
    DbSet<LoginOtp> LoginOtps { get; }
    DbSet<EmailTemplate> EmailTemplates { get; }
    DbSet<EmailOutbox> EmailOutbox { get; }
    DbSet<Tenant> Tenants { get; }

    DbSet<Item> Items { get; }
    DbSet<DeliveryTerm> DeliveryTerms { get; }
    DbSet<PaymentTerm> PaymentTerms { get; }

    DbSet<SupplierEntity> Suppliers { get; }
    DbSet<SupplierVerificationEntity> SupplierVerifications { get; }
    DbSet<SupplierAddressEntity> SupplierAddresses { get; }
    DbSet<SupplierContactEntity> SupplierContacts { get; }

    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    DbSet<DeliverySchedule> DeliverySchedules { get; }
    DbSet<Asn> Asns { get; }
    DbSet<AsnLine> AsnLines { get; }
    DbSet<GoodsReceipt> GoodsReceipts { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<CreditDebitNote> CreditDebitNotes { get; }
    DbSet<Payment> Payments { get; }

    DbSet<DocumentUpload> DocumentUploads { get; }
    DbSet<CommunicationMessage> CommunicationMessages { get; }

    DbSet<InforEndpointMap> InforEndpointMaps { get; }
    DbSet<InforSyncLog> InforSyncLogs { get; }
    DbSet<IntegrationError> IntegrationErrors { get; }

    DbSet<AuditEntry> AuditEntries { get; }

    DbSet<SystemSetting> SystemSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
