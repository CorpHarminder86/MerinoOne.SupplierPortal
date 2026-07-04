namespace MerinoOne.SupplierPortal.Contracts.Authorization;

/// <summary>
/// The single, compiler-checked source of permission-code string literals.
/// Use in <c>[Authorize(Policy = Perm.RoleWrite)]</c> and <c>Token.HasPermission(Perm.RoleWrite)</c>
/// instead of raw strings. The seeded <c>PermissionCatalog</c> is built from these codes; a reflection
/// guard test asserts every code here exists in the catalog and vice-versa.
/// </summary>
public static class Perm
{
    // Supplier
    public const string SupplierRead = "Supplier.Read";
    public const string SupplierInvite = "Supplier.Invite";
    public const string SupplierWrite = "Supplier.Write";
    public const string SupplierApprove = "Supplier.Approve";
    public const string SupplierProvision = "Supplier.Provision";
    public const string SupplierVerifyNic = "Supplier.VerifyNic";
    public const string SupplierChangeRequest = "Supplier.ChangeRequest";
    public const string SupplierApproveChange = "Supplier.ApproveChange";

    // Purchase Order
    public const string PurchaseOrderRead = "PurchaseOrder.Read";
    public const string PurchaseOrderAcknowledge = "PurchaseOrder.Acknowledge";
    public const string PurchaseOrderAccept = "PurchaseOrder.Accept";
    public const string PurchaseOrderNegotiate = "PurchaseOrder.Negotiate";
    public const string PurchaseOrderApproveNegotiation = "PurchaseOrder.ApproveNegotiation";
    public const string PurchaseOrderOverrideGate = "PurchaseOrder.OverrideGate";
    public const string PurchaseOrderWrite = "PurchaseOrder.Write";

    // Delivery Schedule
    public const string DeliveryScheduleRead = "DeliverySchedule.Read";
    public const string DeliverySchedulePropose = "DeliverySchedule.Propose";
    public const string DeliveryScheduleApprove = "DeliverySchedule.Approve";

    // ASN
    public const string AsnRead = "Asn.Read";
    public const string AsnWrite = "Asn.Write";
    public const string AsnApprove = "Asn.Approve";

    // Goods Receipt
    public const string GoodsReceiptRead = "GoodsReceipt.Read";

    // Invoice
    public const string InvoiceRead = "Invoice.Read";
    public const string InvoiceSubmit = "Invoice.Submit";
    public const string InvoiceReview = "Invoice.Review";
    public const string InvoiceApprove = "Invoice.Approve";
    public const string InvoiceRevoke = "Invoice.Revoke";

    // Credit / Debit Note
    public const string CreditDebitNoteRead = "CreditDebitNote.Read";
    public const string CreditDebitNoteWrite = "CreditDebitNote.Write";
    public const string CreditDebitNoteApprove = "CreditDebitNote.Approve";

    // Payment
    public const string PaymentRead = "Payment.Read";

    // Communication
    public const string CommunicationRead = "Communication.Read";
    public const string CommunicationWrite = "Communication.Write";

    // Administration
    public const string UserRead = "User.Read";
    public const string UserWrite = "User.Write";
    public const string RoleRead = "Role.Read";
    public const string RoleWrite = "Role.Write";
    public const string SettingsRead = "Settings.Read";
    public const string SettingsWrite = "Settings.Write";

    // Integration
    public const string IntegrationRead = "Integration.Read";
    public const string IntegrationManage = "Integration.Manage";
    public const string IntegrationApiKeys = "Integration.ApiKeys";

    // R8 (2026-07-04) — Infor IDM document sync. View is WIDE (all roles incl. Supplier — the sync-log screen is
    // RLS-scoped, so each role sees only its own rows). Manage (retry / re-push / backfill) is admin-only.
    public const string IntegrationIdmSyncView = "Integration.IdmSync.View";
    public const string IntegrationIdmSyncManage = "Integration.IdmSync.Manage";

    // Integration — inbound service-to-service scopes (X-APIKey only; not granted to human roles)
    public const string IntegrationInboundErpAck = "Integration.Inbound.ErpAck";
    public const string IntegrationInboundGrn = "Integration.Inbound.Grn";
    public const string IntegrationInboundPayment = "Integration.Inbound.Payment";
    public const string IntegrationInboundInvoiceStatus = "Integration.Inbound.InvoiceStatus";
    public const string IntegrationInboundPo = "Integration.Inbound.Po";
    public const string IntegrationInboundDeliverySchedule = "Integration.Inbound.DeliverySchedule";
    public const string IntegrationInboundGrnReceipt = "Integration.Inbound.GrnReceipt";
    public const string IntegrationInboundTax = "Integration.Inbound.Tax";
    public const string IntegrationInboundPaymentTerm = "Integration.Inbound.PaymentTerm";
    public const string IntegrationInboundDeliveryTerm = "Integration.Inbound.DeliveryTerm";
    public const string IntegrationInboundCurrency = "Integration.Inbound.Currency";
    public const string IntegrationInboundCountry = "Integration.Inbound.Country";
    public const string IntegrationInboundState = "Integration.Inbound.State";
    public const string IntegrationInboundCity = "Integration.Inbound.City";
    public const string IntegrationInboundPostalCode = "Integration.Inbound.PostalCode";
    public const string IntegrationInboundUnit = "Integration.Inbound.Unit";
    public const string IntegrationInboundItemGroup = "Integration.Inbound.ItemGroup";
    public const string IntegrationInboundItem = "Integration.Inbound.Item";

    // Platform (cross-tenant PlatformAdmin only)
    public const string PlatformTenants = "Platform.Tenants";
    public const string PlatformOnboard = "Platform.Onboard";
    public const string PlatformPermissions = "Platform.Permissions";

    /// <summary>Every permission code declared above — for the catalog-parity guard test.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        SupplierRead, SupplierInvite, SupplierWrite, SupplierApprove, SupplierProvision,
        SupplierVerifyNic, SupplierChangeRequest, SupplierApproveChange,
        PurchaseOrderRead, PurchaseOrderAcknowledge, PurchaseOrderAccept, PurchaseOrderNegotiate,
        PurchaseOrderApproveNegotiation, PurchaseOrderOverrideGate, PurchaseOrderWrite,
        DeliveryScheduleRead, DeliverySchedulePropose, DeliveryScheduleApprove,
        AsnRead, AsnWrite, AsnApprove,
        GoodsReceiptRead,
        InvoiceRead, InvoiceSubmit, InvoiceReview, InvoiceApprove, InvoiceRevoke,
        CreditDebitNoteRead, CreditDebitNoteWrite, CreditDebitNoteApprove,
        PaymentRead,
        CommunicationRead, CommunicationWrite,
        UserRead, UserWrite, RoleRead, RoleWrite, SettingsRead, SettingsWrite,
        IntegrationRead, IntegrationManage, IntegrationApiKeys,
        IntegrationIdmSyncView, IntegrationIdmSyncManage,
        IntegrationInboundErpAck, IntegrationInboundGrn, IntegrationInboundPayment,
        IntegrationInboundInvoiceStatus, IntegrationInboundPo, IntegrationInboundDeliverySchedule,
        IntegrationInboundGrnReceipt, IntegrationInboundTax,
        IntegrationInboundPaymentTerm, IntegrationInboundDeliveryTerm, IntegrationInboundCurrency,
        IntegrationInboundCountry, IntegrationInboundState, IntegrationInboundCity,
        IntegrationInboundPostalCode, IntegrationInboundUnit, IntegrationInboundItemGroup,
        IntegrationInboundItem,
        PlatformTenants, PlatformOnboard, PlatformPermissions,
    };
}
