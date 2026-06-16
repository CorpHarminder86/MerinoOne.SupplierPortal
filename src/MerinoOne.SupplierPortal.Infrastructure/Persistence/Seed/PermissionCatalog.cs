namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class PermissionCatalog
{
    public record PermissionSeed(string Code, string Name, string Module, string Description);

    public static readonly IReadOnlyList<PermissionSeed> All = new[]
    {
        new PermissionSeed("Supplier.Read",                "View suppliers",            "Supplier",       "View supplier records"),
        new PermissionSeed("Supplier.Invite",              "Invite supplier",           "Supplier",       "Send an onboarding invitation"),
        new PermissionSeed("Supplier.Write",               "Create/edit supplier",      "Supplier",       "Create or edit supplier details"),
        new PermissionSeed("Supplier.Approve",             "Approve supplier",          "Supplier",       "Approve or reject a supplier registration"),
        new PermissionSeed("Supplier.Provision",           "Provision supplier users",  "Supplier",       "Map portal users to suppliers"),
        new PermissionSeed("Supplier.VerifyNic",           "Verify NIC",                "Supplier",       "Trigger NIC verification of GST/PAN/MSME"),
        new PermissionSeed("PurchaseOrder.Read",           "View POs",                  "Purchase Order", "View purchase orders"),
        new PermissionSeed("PurchaseOrder.Acknowledge",    "Acknowledge PO",            "Purchase Order", "Acknowledge receipt of a PO"),
        new PermissionSeed("PurchaseOrder.Accept",         "Accept PO",                 "Purchase Order", "Accept, reject or propose a date on a PO"),
        new PermissionSeed("PurchaseOrder.ApproveProposal","Approve PO proposal",       "Purchase Order", "Approve a supplier-proposed delivery date"),
        new PermissionSeed("PurchaseOrder.Write",          "Create/update PO",          "Purchase Order", "Create or update PO documents (ERP sync, admin)"),
        new PermissionSeed("DeliverySchedule.Read",        "View delivery schedules",   "Shipment",       "View delivery schedules"),
        new PermissionSeed("DeliverySchedule.Propose",     "Propose delivery schedule", "Shipment",       "Propose a delivery schedule"),
        new PermissionSeed("DeliverySchedule.Approve",     "Approve delivery schedule", "Shipment",       "Approve or reject a delivery schedule"),
        new PermissionSeed("Asn.Read",                     "View ASNs",                 "Shipment",       "View ASNs"),
        new PermissionSeed("Asn.Write",                    "Create/update ASN",         "Shipment",       "Create or update ASNs"),
        new PermissionSeed("GoodsReceipt.Read",            "View GRNs",                 "Shipment",       "View goods-receipt (GRN) data"),
        new PermissionSeed("Invoice.Read",                 "View invoices",             "Invoice",        "View invoices"),
        new PermissionSeed("Invoice.Submit",               "Submit invoice",            "Invoice",        "Submit an invoice"),
        new PermissionSeed("Invoice.Review",               "Review invoice",            "Invoice",        "Mark an invoice under review"),
        new PermissionSeed("Invoice.Approve",              "Approve invoice",           "Invoice",        "Approve or reject an invoice"),
        new PermissionSeed("CreditDebitNote.Read",         "View CN/DN",                "Invoice",        "View credit / debit notes"),
        new PermissionSeed("CreditDebitNote.Write",        "Create CN/DN",              "Invoice",        "Create a credit / debit note"),
        new PermissionSeed("CreditDebitNote.Approve",      "Approve CN/DN",             "Invoice",        "Approve a credit / debit note"),
        new PermissionSeed("Payment.Read",                 "View payments",             "Payment",        "View payments and remittance advice"),
        new PermissionSeed("Communication.Read",           "View messages",             "Communication",  "View messages and threads"),
        new PermissionSeed("Communication.Write",          "Send messages",             "Communication",  "Send messages"),
        new PermissionSeed("User.Read",                    "View users",                "Administration", "View portal users"),
        new PermissionSeed("User.Write",                   "Manage users",              "Administration", "Create and manage portal users"),
        new PermissionSeed("Role.Read",                    "View roles",                "Administration", "View roles and permissions"),
        new PermissionSeed("Role.Write",                   "Manage roles",              "Administration", "Manage roles and role-permission assignments"),
        new PermissionSeed("Settings.Read",                "View settings",             "Administration", "View settings (dropdowns, templates)"),
        new PermissionSeed("Settings.Write",               "Manage settings",           "Administration", "Manage settings"),
        new PermissionSeed("Integration.Read",             "View integration",          "Integration",    "View Infor endpoints, sync log and errors"),
        new PermissionSeed("Integration.Manage",           "Manage integration",        "Integration",    "Retry integration errors, manage endpoint mapping"),
        new PermissionSeed("Integration.ApiKeys",          "Manage API keys",           "Integration",    "Generate, rotate and revoke inbound X-APIKey credentials"),
        // Platform-tier permissions — held ONLY by the cross-tenant PlatformAdmin (separation of duties:
        // a Platform Admin onboards tenants/companies/first-admins but reads NO business data).
        new PermissionSeed("Platform.Tenants",             "Manage tenants",            "Platform",       "View and manage tenants and their companies (cross-tenant)"),
        new PermissionSeed("Platform.Onboard",             "Onboard tenant",            "Platform",       "Onboard a tenant: create tenant, companies and the first Tenant Admin"),
    };

    public static readonly string[] Roles = { "PlatformAdmin", "SuperAdmin", "Admin", "Buyer", "Finance", "Supplier", "ReadOnly" };

    // permission code -> roles that hold it (matches TSD §7.2 matrix)
    public static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
    {
        ["Supplier.Read"]                 = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Supplier.Invite"]               = new[] { "SuperAdmin","Admin","Buyer" },
        ["Supplier.Write"]                = new[] { "SuperAdmin","Admin","Supplier" },
        ["Supplier.Approve"]              = new[] { "SuperAdmin","Admin" },
        ["Supplier.Provision"]            = new[] { "SuperAdmin","Admin" },
        ["Supplier.VerifyNic"]            = new[] { "SuperAdmin","Admin" },
        ["PurchaseOrder.Read"]            = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["PurchaseOrder.Acknowledge"]     = new[] { "SuperAdmin","Supplier" },
        ["PurchaseOrder.Accept"]          = new[] { "SuperAdmin","Supplier" },
        ["PurchaseOrder.ApproveProposal"] = new[] { "SuperAdmin","Buyer" },
        ["PurchaseOrder.Write"]           = new[] { "SuperAdmin","Buyer" },
        ["DeliverySchedule.Read"]         = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["DeliverySchedule.Propose"]      = new[] { "SuperAdmin","Supplier" },
        ["DeliverySchedule.Approve"]      = new[] { "SuperAdmin","Buyer" },
        ["Asn.Read"]                      = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Asn.Write"]                     = new[] { "SuperAdmin","Supplier" },
        ["GoodsReceipt.Read"]             = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Invoice.Read"]                  = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Invoice.Submit"]                = new[] { "SuperAdmin","Supplier" },
        ["Invoice.Review"]                = new[] { "SuperAdmin","Finance" },
        ["Invoice.Approve"]               = new[] { "SuperAdmin","Finance" },
        ["CreditDebitNote.Read"]          = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["CreditDebitNote.Write"]         = new[] { "SuperAdmin","Supplier" },
        ["CreditDebitNote.Approve"]       = new[] { "SuperAdmin","Finance" },
        ["Payment.Read"]                  = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Communication.Read"]            = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier","ReadOnly" },
        ["Communication.Write"]           = new[] { "SuperAdmin","Admin","Buyer","Finance","Supplier" },
        ["User.Read"]                     = new[] { "SuperAdmin","Admin" },
        ["User.Write"]                    = new[] { "SuperAdmin","Admin" },
        ["Role.Read"]                     = new[] { "SuperAdmin","Admin" },
        ["Role.Write"]                    = new[] { "SuperAdmin" },
        ["Settings.Read"]                 = new[] { "SuperAdmin","Admin" },
        ["Settings.Write"]                = new[] { "SuperAdmin","Admin" },
        ["Integration.Read"]              = new[] { "SuperAdmin","Admin" },
        ["Integration.Manage"]            = new[] { "SuperAdmin","Admin" },
        ["Integration.ApiKeys"]           = new[] { "SuperAdmin","Admin" },
        // Platform-tier perms granted ONLY to PlatformAdmin. PlatformAdmin appears in NO other row —
        // it holds zero business-data permissions (separation of duties).
        ["Platform.Tenants"]              = new[] { "PlatformAdmin" },
        ["Platform.Onboard"]              = new[] { "PlatformAdmin" },
    };
}
