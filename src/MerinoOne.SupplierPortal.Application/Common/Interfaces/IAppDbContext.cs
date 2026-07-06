using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierVerificationEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;
using SupplierBankDetailEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierBankDetail;
using SupplierLicenseEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierLicense;
using SupplierChangeRequestEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequest;
using SupplierChangeRequestLineEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequestLine;

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
    DbSet<TenantEntity> TenantEntities { get; }
    DbSet<UserCompanyMap> UserCompanyMaps { get; }

    // R5 (TSD R5 Addendum §4.1–4.2 / Component 1) — named, ERP-mappable ship-to addresses hung off the
    // TenantEntity (the customer/buying entity). Admin config master (Settings-gated).
    DbSet<CompanyAddress> CompanyAddresses { get; }

    DbSet<Item> Items { get; }
    DbSet<SupplierItem> SupplierItems { get; }
    DbSet<ItemGroup> ItemGroups { get; }
    DbSet<Unit> Units { get; }
    DbSet<DeliveryTerm> DeliveryTerms { get; }
    DbSet<PaymentTerm> PaymentTerms { get; }

    DbSet<Currency> Currencies { get; }
    DbSet<Country> Countries { get; }
    DbSet<State> States { get; }
    DbSet<City> Cities { get; }
    DbSet<PostalCode> PostalCodes { get; }

    DbSet<SupplierEntity> Suppliers { get; }
    DbSet<SupplierVerificationEntity> SupplierVerifications { get; }
    DbSet<SupplierAddressEntity> SupplierAddresses { get; }
    DbSet<SupplierContactEntity> SupplierContacts { get; }
    DbSet<SupplierBankDetailEntity> SupplierBankDetails { get; }
    DbSet<SupplierLicenseEntity> SupplierLicenses { get; }
    DbSet<SupplierChangeRequestEntity> SupplierChangeRequests { get; }
    DbSet<SupplierChangeRequestLineEntity> SupplierChangeRequestLines { get; }

    DbSet<Tax> Taxes { get; }

    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    DbSet<PurchaseOrderNegotiation> PurchaseOrderNegotiations { get; }
    DbSet<PurchaseOrderNegotiationLine> PurchaseOrderNegotiationLines { get; }
    DbSet<DeliverySchedule> DeliverySchedules { get; }
    DbSet<Asn> Asns { get; }
    DbSet<AsnLine> AsnLines { get; }
    DbSet<AsnLineSerial> AsnLineSerials { get; }
    DbSet<AsnLineLot> AsnLineLots { get; }
    DbSet<AsnPurchaseOrder> AsnPurchaseOrders { get; }
    // R5 (TSD R5 Addendum §4.6 / Component 6) — ASN approval sessions.
    DbSet<AsnApproval> AsnApprovals { get; }
    DbSet<GoodsReceipt> GoodsReceipts { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<CreditDebitNote> CreditDebitNotes { get; }
    DbSet<Payment> Payments { get; }

    // R5 (TSD R5 Addendum §4.7) — ERP→portal status mapping master.
    DbSet<PoStatusMapping> PoStatusMappings { get; }

    DbSet<DocumentUpload> DocumentUploads { get; }
    DbSet<AttachmentType> AttachmentTypes { get; }
    DbSet<AttachmentEntity> AttachmentEntities { get; }
    DbSet<AttachmentRequirementPolicy> AttachmentRequirementPolicies { get; }
    DbSet<CommunicationMessage> CommunicationMessages { get; }

    DbSet<InforEndpointMap> InforEndpointMaps { get; }
    DbSet<InforSyncLog> InforSyncLogs { get; }
    DbSet<IntegrationError> IntegrationErrors { get; }
    DbSet<CompanyShareGroup> CompanyShareGroups { get; }
    DbSet<CompanyShareGroupMember> CompanyShareGroupMembers { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<ApiKeyCompany> ApiKeyCompanies { get; }
    DbSet<InforConnectionSetting> InforConnectionSettings { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    // R8 (2026-07-04) — TSD R8 outbound document sync to Infor IDM.
    DbSet<OutboundEndpointConfig> OutboundEndpointConfigs { get; }
    DbSet<IdmAttachmentTypeConfig> IdmAttachmentTypeConfigs { get; }
    DbSet<IdmDocumentOutbox> IdmDocumentOutboxes { get; }

    // R9 (2026-07-06) — TSD R9 config-driven LN outbound posting.
    DbSet<LnEndpointConfig> LnEndpointConfigs { get; }
    DbSet<IntegrationSwitch> IntegrationSwitches { get; }
    DbSet<IntegrationSwitchAudit> IntegrationSwitchAudits { get; }
    DbSet<LnBackfillRun> LnBackfillRuns { get; }
    DbSet<HeldInboundMessage> HeldInboundMessages { get; }

    DbSet<AuditEntry> AuditEntries { get; }

    DbSet<SystemSetting> SystemSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Begins an explicit DB transaction. Used by the inbound integration upsert path so the row
    /// upserts + the InforSyncLog / IntegrationError write + the endpoint session-column update all
    /// commit (or roll back) atomically. Delegates to the underlying EF context's relational
    /// transaction so callers in the Application layer don't need an Infrastructure reference.
    /// </summary>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>
    /// Detaches all tracked entities. Used by the inbound integration path after a failed/rolled-back
    /// transaction so a follow-up "record the failure" SaveChanges starts from a clean change tracker
    /// (the previously-tracked Added/Modified entities must not be re-attempted).
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// FIX #1 — the currently tracked change-tracker entries. The inbound poison-isolation path snapshots the
    /// pending Added/Modified business entities after a batched flush fails so it can re-flush them individually
    /// and pinpoint the genuinely-failing rows (the provider batches inserts into a MERGE that cannot attribute a
    /// constraint failure to one row).
    /// </summary>
    IReadOnlyList<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry> ChangeTrackerEntries();

    /// <summary>
    /// FIX #1 — attaches a previously-detached entity and returns its entry so the poison-isolation path can replay
    /// it under its original state in an individual flush. Mirrors <c>DbContext.Attach(object)</c>.
    /// </summary>
    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Attach(object entity);
}
