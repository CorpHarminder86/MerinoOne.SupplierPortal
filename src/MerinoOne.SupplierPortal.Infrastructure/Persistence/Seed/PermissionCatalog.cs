using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class PermissionCatalog
{
    public record PermissionSeed(string Code, string Name, string Module, string Description);

    public static readonly IReadOnlyList<PermissionSeed> All = new[]
    {
        new PermissionSeed(Perm.SupplierRead,                "View suppliers",            "Supplier",       "View supplier records"),
        new PermissionSeed(Perm.SupplierInvite,              "Invite supplier",           "Supplier",       "Send an onboarding invitation"),
        new PermissionSeed(Perm.SupplierWrite,               "Create/edit supplier",      "Supplier",       "Create or edit supplier details"),
        new PermissionSeed(Perm.SupplierApprove,             "Approve supplier",          "Supplier",       "Approve or reject a supplier registration"),
        new PermissionSeed(Perm.SupplierProvision,           "Provision supplier users",  "Supplier",       "Map portal users to suppliers"),
        new PermissionSeed(Perm.SupplierVerifyNic,           "Verify NIC",                "Supplier",       "Trigger NIC verification of GST/PAN/MSME"),
        // R4 Module 2 — supplier change-management lifecycle (supplier raises; internal approves).
        new PermissionSeed(Perm.SupplierChangeRequest,       "Raise supplier change",     "Supplier",       "Raise a post-registration supplier change request (and edit own bank/license records)"),
        new PermissionSeed(Perm.SupplierApproveChange,       "Approve supplier change",   "Supplier",       "Review/approve/reject a supplier change request"),
        new PermissionSeed(Perm.PurchaseOrderRead,           "View POs",                  "Purchase Order", "View purchase orders"),
        new PermissionSeed(Perm.PurchaseOrderAcknowledge,    "Acknowledge PO",            "Purchase Order", "Acknowledge receipt of a PO"),
        new PermissionSeed(Perm.PurchaseOrderAccept,         "Accept PO",                 "Purchase Order", "Accept or reject a PO"),
        // R4 (2026-06-24) — PO negotiation lifecycle (supplier raises; buyer approves/rejects).
        // R4 (2026-06-26) — D2: PurchaseOrder.ApproveProposal REMOVED (the date-only propose/approve flow is retired;
        // PO negotiation replaces it). 2b removes the seeded permission + role-permission rows.
        new PermissionSeed(Perm.PurchaseOrderNegotiate,          "Negotiate PO",          "Purchase Order", "Raise a PO negotiation (change qty/delivery date)"),
        new PermissionSeed(Perm.PurchaseOrderApproveNegotiation, "Approve PO negotiation","Purchase Order", "Review/approve/reject a PO negotiation"),
        // R4 (2026-06-26) — §6.5 / UC-PO-09: admin-only PO confirmation-gate override (ship despite the gate with a
        // mandatory reason + audited override row). Granted SuperAdmin + Admin only.
        new PermissionSeed(Perm.PurchaseOrderOverrideGate,   "Override PO ship gate",     "Purchase Order", "Override the PO confirmation gate to ship despite an unconfirmed/contested PO (mandatory reason, audited)"),
        new PermissionSeed(Perm.PurchaseOrderWrite,          "Create/update PO",          "Purchase Order", "Create or update PO documents (ERP sync, admin)"),
        new PermissionSeed(Perm.DeliveryScheduleRead,        "View delivery schedules",   "Shipment",       "View delivery schedules"),
        new PermissionSeed(Perm.DeliverySchedulePropose,     "Propose delivery schedule", "Shipment",       "Propose a delivery schedule"),
        new PermissionSeed(Perm.DeliveryScheduleApprove,     "Approve delivery schedule", "Shipment",       "Approve or reject a delivery schedule"),
        new PermissionSeed(Perm.AsnRead,                     "View ASNs",                 "Shipment",       "View ASNs"),
        new PermissionSeed(Perm.AsnWrite,                    "Create/update ASN",         "Shipment",       "Create or update ASNs"),
        // R5 (TSD R5 Addendum §10.2) — buyer approval of a PendingApproval ASN (approve→submit / reject+reason).
        new PermissionSeed(Perm.AsnApprove,                  "Approve/reject ASN",        "Shipment",       "Approve or reject an ASN sent for buyer approval"),
        new PermissionSeed(Perm.GoodsReceiptRead,            "View GRNs",                 "Shipment",       "View goods-receipt (GRN) data"),
        new PermissionSeed(Perm.InvoiceRead,                 "View invoices",             "Invoice",        "View invoices"),
        new PermissionSeed(Perm.InvoiceSubmit,               "Submit invoice",            "Invoice",        "Submit an invoice"),
        new PermissionSeed(Perm.InvoiceReview,               "Review invoice",            "Invoice",        "Mark an invoice under review"),
        new PermissionSeed(Perm.InvoiceApprove,              "Approve invoice",           "Invoice",        "Approve or reject an invoice"),
        // R4 Module 4 — admin pre-post invoice revoke (Submitted -> Draft).
        new PermissionSeed(Perm.InvoiceRevoke,               "Revoke invoice",            "Invoice",        "Admin revoke a submitted (pre-post) invoice back to Draft"),
        new PermissionSeed(Perm.CreditDebitNoteRead,         "View CN/DN",                "Invoice",        "View credit / debit notes"),
        new PermissionSeed(Perm.CreditDebitNoteWrite,        "Create CN/DN",              "Invoice",        "Create a credit / debit note"),
        new PermissionSeed(Perm.CreditDebitNoteApprove,      "Approve CN/DN",             "Invoice",        "Approve a credit / debit note"),
        new PermissionSeed(Perm.PaymentRead,                 "View payments",             "Payment",        "View payments and remittance advice"),
        new PermissionSeed(Perm.CommunicationRead,           "View messages",             "Communication",  "View messages and threads"),
        new PermissionSeed(Perm.CommunicationWrite,          "Send messages",             "Communication",  "Send messages"),
        new PermissionSeed(Perm.DocumentRead,                "View documents",            "Communication",  "View the cross-module attachment register (RLS-scoped)"),
        new PermissionSeed(Perm.UserRead,                    "View users",                "Administration", "View portal users"),
        new PermissionSeed(Perm.UserWrite,                   "Manage users",              "Administration", "Create and manage portal users"),
        new PermissionSeed(Perm.RoleRead,                    "View roles",                "Administration", "View roles and permissions"),
        new PermissionSeed(Perm.RoleWrite,                   "Manage roles",              "Administration", "Manage roles and role-permission assignments"),
        new PermissionSeed(Perm.SettingsRead,                "View settings",             "Administration", "View settings (dropdowns, templates)"),
        new PermissionSeed(Perm.SettingsWrite,               "Manage settings",           "Administration", "Manage settings"),
        new PermissionSeed(Perm.IntegrationRead,             "View integration",          "Integration",    "View Infor endpoints, sync log and errors"),
        new PermissionSeed(Perm.IntegrationManage,           "Manage integration",        "Integration",    "Retry integration errors, manage endpoint mapping"),
        new PermissionSeed(Perm.IntegrationApiKeys,          "Manage API keys",           "Integration",    "Generate, rotate and revoke inbound X-APIKey credentials"),
        // R8 (2026-07-04) — Infor IDM document sync monitoring + operations.
        new PermissionSeed(Perm.IntegrationIdmSyncView,      "View IDM document sync",    "Integration",    "View the Infor IDM document sync log (RLS-scoped per role)"),
        new PermissionSeed(Perm.IntegrationIdmSyncManage,    "Manage IDM document sync",  "Integration",    "Retry, re-push and backfill Infor IDM document sync"),
        // R4 cross-cutting — service-to-service inbound scopes (X-APIKey). NOT granted to any human role; bound to
        // API keys via their scope list. Seeded here so the catalogue is complete and the inbound endpoint-gate resolves.
        new PermissionSeed(Perm.IntegrationInboundErpAck,       "Inbound: ERP ack",          "Integration", "Inbound /erp-ack: ERP acknowledges a Portal->ERP transaction (writes back erpCode)"),
        new PermissionSeed(Perm.IntegrationInboundGrn,          "Inbound: GRN status",       "Integration", "Inbound /grn-status: ERP pushes goods-receipt status"),
        new PermissionSeed(Perm.IntegrationInboundPayment,      "Inbound: payments",         "Integration", "Inbound /payments: ERP pushes payment / remittance data"),
        new PermissionSeed(Perm.IntegrationInboundInvoiceStatus,"Inbound: invoice status",   "Integration", "Inbound /invoice-status: ERP advances invoice to Matched/Approved/Paid"),
        new PermissionSeed(Perm.IntegrationInboundPo,           "Inbound: purchase orders",  "Integration", "Inbound /purchase-orders: ERP pushes/creates Purchase Orders + lines"),
        new PermissionSeed(Perm.IntegrationInboundDeliverySchedule,"Inbound: delivery schedules","Integration","Inbound /delivery-schedules: ERP pushes PO delivery schedules"),
        new PermissionSeed(Perm.IntegrationInboundGrnReceipt,   "Inbound: goods receipts",   "Integration", "Inbound /goods-receipts: ERP creates GRN rows against PO lines"),
        new PermissionSeed(Perm.IntegrationInboundTax,          "Inbound: tax codes",        "Integration", "Inbound /taxes: ERP pushes company-shared tax-code master"),
        new PermissionSeed(Perm.IntegrationInboundPaymentTerm,  "Inbound: payment terms",    "Integration", "Inbound /payment-terms: ERP pushes Payment Term master"),
        new PermissionSeed(Perm.IntegrationInboundDeliveryTerm, "Inbound: delivery terms",   "Integration", "Inbound /delivery-terms: ERP pushes Delivery Term master"),
        new PermissionSeed(Perm.IntegrationInboundCurrency,     "Inbound: currencies",       "Integration", "Inbound /currencies: ERP pushes currency master"),
        new PermissionSeed(Perm.IntegrationInboundCountry,      "Inbound: countries",        "Integration", "Inbound /countries: ERP pushes country master"),
        new PermissionSeed(Perm.IntegrationInboundState,        "Inbound: states",           "Integration", "Inbound /states: ERP pushes state master"),
        new PermissionSeed(Perm.IntegrationInboundCity,         "Inbound: cities",           "Integration", "Inbound /cities: ERP pushes city master"),
        new PermissionSeed(Perm.IntegrationInboundPostalCode,   "Inbound: postal codes",     "Integration", "Inbound /postal-codes: ERP pushes postal-code master"),
        new PermissionSeed(Perm.IntegrationInboundUnit,         "Inbound: units",            "Integration", "Inbound /units: ERP pushes unit-of-measure master"),
        new PermissionSeed(Perm.IntegrationInboundItemGroup,    "Inbound: item groups",      "Integration", "Inbound /item-groups: ERP pushes item-group master"),
        new PermissionSeed(Perm.IntegrationInboundItem,         "Inbound: items",            "Integration", "Inbound /items: ERP pushes company-scoped item master"),
        // Platform-tier permissions — held ONLY by the cross-tenant PlatformAdmin (separation of duties:
        // a Platform Admin onboards tenants/companies/first-admins but reads NO business data).
        new PermissionSeed(Perm.PlatformTenants,             "Manage tenants",            "Platform",       "View and manage tenants and their companies (cross-tenant)"),
        new PermissionSeed(Perm.PlatformOnboard,             "Onboard tenant",            "Platform",       "Onboard a tenant: create tenant, companies and the first Tenant Admin"),
        new PermissionSeed(Perm.PlatformPermissions,         "Manage permission catalog", "Platform",       "Create new global permission codes in the catalog (platform operator only)"),
    };

    public static readonly string[] Roles = RoleNames.BuiltIn;

    // permission code -> roles that hold it (matches TSD §7.2 matrix)
    public static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
    {
        [Perm.SupplierRead]                 = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.SupplierInvite]               = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer },
        [Perm.SupplierWrite]                = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Supplier },
        [Perm.SupplierApprove]              = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.SupplierProvision]            = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.SupplierVerifyNic]            = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.SupplierChangeRequest]        = new[] { RoleNames.Supplier },
        [Perm.SupplierApproveChange]        = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.PurchaseOrderRead]            = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.PurchaseOrderAcknowledge]     = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.PurchaseOrderAccept]          = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.PurchaseOrderNegotiate]          = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.PurchaseOrderApproveNegotiation] = new[] { RoleNames.SuperAdmin, RoleNames.Buyer },
        // R4 (2026-06-26) — §6.5: gate override is admin-only (SuperAdmin + Admin).
        [Perm.PurchaseOrderOverrideGate]    = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.PurchaseOrderWrite]           = new[] { RoleNames.SuperAdmin, RoleNames.Buyer },
        [Perm.DeliveryScheduleRead]         = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.DeliverySchedulePropose]      = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.DeliveryScheduleApprove]      = new[] { RoleNames.SuperAdmin, RoleNames.Buyer },
        [Perm.AsnRead]                      = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.AsnWrite]                     = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.AsnApprove]                   = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer },
        [Perm.GoodsReceiptRead]             = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.InvoiceRead]                  = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.InvoiceSubmit]                = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.InvoiceReview]                = new[] { RoleNames.SuperAdmin, RoleNames.Finance },
        [Perm.InvoiceApprove]               = new[] { RoleNames.SuperAdmin, RoleNames.Finance },
        [Perm.InvoiceRevoke]                = new[] { RoleNames.SuperAdmin, RoleNames.Finance },
        [Perm.CreditDebitNoteRead]          = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.CreditDebitNoteWrite]         = new[] { RoleNames.SuperAdmin, RoleNames.Supplier },
        [Perm.CreditDebitNoteApprove]       = new[] { RoleNames.SuperAdmin, RoleNames.Finance },
        [Perm.PaymentRead]                  = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.CommunicationRead]            = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.CommunicationWrite]           = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier },
        // Cross-module document register — all roles (RLS scopes each to its own seccode rows).
        [Perm.DocumentRead]                 = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.UserRead]                     = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.UserWrite]                    = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.RoleRead]                     = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.RoleWrite]                    = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.SettingsRead]                 = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.SettingsWrite]                = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.IntegrationRead]              = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.IntegrationManage]            = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        [Perm.IntegrationApiKeys]           = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        // R8 — View is wide (RLS scopes each role's rows); Manage is admin-only.
        [Perm.IntegrationIdmSyncView]       = new[] { RoleNames.SuperAdmin, RoleNames.Admin, RoleNames.Buyer, RoleNames.Finance, RoleNames.Supplier, RoleNames.ReadOnly },
        [Perm.IntegrationIdmSyncManage]     = new[] { RoleNames.SuperAdmin, RoleNames.Admin },
        // Platform-tier perms granted ONLY to PlatformAdmin. PlatformAdmin appears in NO other row —
        // it holds zero business-data permissions (separation of duties).
        [Perm.PlatformTenants]              = new[] { RoleNames.PlatformAdmin },
        [Perm.PlatformOnboard]              = new[] { RoleNames.PlatformAdmin },
        [Perm.PlatformPermissions]          = new[] { RoleNames.PlatformAdmin },
    };
}
