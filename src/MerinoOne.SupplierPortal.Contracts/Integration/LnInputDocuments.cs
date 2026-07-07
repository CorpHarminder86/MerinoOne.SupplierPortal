using System.Text.Json.Serialization;

namespace MerinoOne.SupplierPortal.Contracts.Integration;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// TSD R9 (D-R9-7) — frozen LN input-document contracts, one per portalEntity. These are the ONLY
// shapes the JSONata eligibility gates and request mappings evaluate against, and the schema the
// Phase D editor picker reflects — NEVER the raw EF entities. Field names are a public contract:
// renaming one is a breaking change that must bump the matching LnInputDocumentVersions constant
// (pinned samples carry the stamp; the config screen shows a stale badge on drift, D-R9-18).
//
// Date/time fields carry the exact pre-formatted strings the legacy payload builders emit ("o" or
// "yyyy-MM-dd") so request expressions are pure projection — no formatting in JSONata. Serialized
// with nulls KEPT (unlike the legacy WhenWritingNull payloads) so gates and the picker see every
// field; request expressions navigate missing/null fields to reproduce the legacy null-dropping.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Portal-entity discriminators for <c>integration.OutboundIntegrationConfig.portalEntity</c>. APPEND-ONLY.</summary>
public static class LnPortalEntity
{
    public const string Invoice = "Invoice";
    public const string Asn = "Asn";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string Supplier = "Supplier";
    public const string SupplierChange = "SupplierChange";
    public const string PoNegotiation = "PoNegotiation";
}

/// <summary>
/// Builder-version stamps (D-R9-18). Bump when the matching input-document contract changes shape;
/// pinned samples stamped with an older version render a "sample stale — re-snapshot" badge.
/// </summary>
public static class LnInputDocumentVersions
{
    public const string Invoice = "invoice-v1";
    public const string Asn = "asn-v1";
    public const string PurchaseOrder = "purchaseOrder-v1";
    public const string Supplier = "supplier-v1";
    public const string SupplierChange = "supplierChange-v1";
    public const string PoNegotiation = "poNegotiation-v1";

    /// <summary>Current version for a portalEntity (throws on unknown — config rows are registry-validated).</summary>
    public static string For(string portalEntity) => portalEntity switch
    {
        LnPortalEntity.Invoice => Invoice,
        LnPortalEntity.Asn => Asn,
        LnPortalEntity.PurchaseOrder => PurchaseOrder,
        LnPortalEntity.Supplier => Supplier,
        LnPortalEntity.SupplierChange => SupplierChange,
        LnPortalEntity.PoNegotiation => PoNegotiation,
        _ => throw new ArgumentOutOfRangeException(nameof(portalEntity), portalEntity, "Unknown LN portal entity."),
    };
}

/// <summary>Invoice input document (portalEntity <c>Invoice</c>, transaction <c>InvoicePost</c>).</summary>
public sealed record InvoiceInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
    [property: JsonPropertyName("invoiceDate")] string InvoiceDate,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("invoiceAmount")] decimal InvoiceAmount,
    [property: JsonPropertyName("taxAmount")] decimal TaxAmount,
    [property: JsonPropertyName("netAmount")] decimal NetAmount,
    [property: JsonPropertyName("eInvoiceIrn")] string? EInvoiceIrn,
    [property: JsonPropertyName("invoiceStatus")] string InvoiceStatus,
    [property: JsonPropertyName("grnReference")] string? GrnReference,
    [property: JsonPropertyName("erpCode")] string? ErpCode,
    [property: JsonPropertyName("erpCompany")] string? ErpCompany,
    [property: JsonPropertyName("erpTransactionType")] string? ErpTransactionType,
    [property: JsonPropertyName("erpDocumentNo")] string? ErpDocumentNo,
    [property: JsonPropertyName("erpPostInitiatedAt")] string? ErpPostInitiatedAt,
    [property: JsonPropertyName("erpPostedAt")] string? ErpPostedAt,
    [property: JsonPropertyName("supplierId")] Guid SupplierId,
    [property: JsonPropertyName("supplierCode")] string? SupplierCode,
    [property: JsonPropertyName("supplierErpCode")] string? SupplierErpCode,
    // Cross-entity GRN coverage summary — the Phase B gate's only window onto GoodsReceipt state
    // (the GRN condition lives in the gate, never in the candidate filter — TSD §2.5a).
    [property: JsonPropertyName("hasCoveringGrns")] bool HasCoveringGrns,
    [property: JsonPropertyName("allCoveringGrnsApproved")] bool AllCoveringGrnsApproved);

/// <summary>ASN input document (portalEntity <c>Asn</c>, transaction <c>AsnPost</c>).</summary>
public sealed record AsnInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("asnNumber")] string AsnNumber,
    [property: JsonPropertyName("expectedDeliveryDate")] string ExpectedDeliveryDate,
    [property: JsonPropertyName("carrierName")] string? CarrierName,
    [property: JsonPropertyName("trackingNumber")] string? TrackingNumber,
    [property: JsonPropertyName("vehicleNumber")] string? VehicleNumber,
    [property: JsonPropertyName("asnStatus")] string AsnStatus,
    [property: JsonPropertyName("erpCode")] string? ErpCode,
    [property: JsonPropertyName("erpCompany")] string? ErpCompany,
    [property: JsonPropertyName("erpTransactionType")] string? ErpTransactionType,
    [property: JsonPropertyName("erpDocumentNo")] string? ErpDocumentNo,
    [property: JsonPropertyName("lines")] IReadOnlyList<AsnLineInputDoc> Lines);

public sealed record AsnLineInputDoc(
    [property: JsonPropertyName("positionNo")] int? PositionNo,
    [property: JsonPropertyName("sequenceNo")] int? SequenceNo,
    [property: JsonPropertyName("itemCode")] string? ItemCode,
    [property: JsonPropertyName("shippedQty")] decimal ShippedQty,
    [property: JsonPropertyName("batchNumber")] string? BatchNumber,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate,
    // Null (not empty) when the line carries no per-unit capture — request expressions reproduce the
    // legacy WhenWritingNull omission by navigating the missing value.
    [property: JsonPropertyName("serials")] IReadOnlyList<string>? Serials,
    [property: JsonPropertyName("lots")] IReadOnlyList<AsnLotInputDoc>? Lots);

public sealed record AsnLotInputDoc(
    [property: JsonPropertyName("lotNo")] string LotNo,
    [property: JsonPropertyName("qty")] decimal Qty,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate);

/// <summary>
/// Purchase-order input document — shared by the three PO-response transactions
/// (<c>PoAcknowledge</c>/<c>PoAccept</c>/<c>PoReject</c>). <see cref="ResponseContext"/> folds the
/// per-row parameters historically carried on <c>OutboxMessage.PayloadJson</c> (proposed date, reject
/// reason) into the document so gate + request read one root (D-R9-7).
/// </summary>
public sealed record PurchaseOrderInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("poNumber")] string PoNumber,
    [property: JsonPropertyName("poStatus")] string PoStatus,
    [property: JsonPropertyName("erpStatus")] string? ErpStatus,
    [property: JsonPropertyName("acknowledgmentAt")] string? AcknowledgmentAt,
    [property: JsonPropertyName("acceptedAt")] string? AcceptedAt,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    [property: JsonPropertyName("responseContext")] PoResponseContextInputDoc ResponseContext);

public sealed record PoResponseContextInputDoc(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("proposedDeliveryDate")] string? ProposedDeliveryDate,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>Supplier master input document (portalEntity <c>Supplier</c>, transaction <c>SupplierSync</c>).</summary>
public sealed record SupplierInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("supplierCode")] string SupplierCode,
    [property: JsonPropertyName("erpCode")] string? ErpCode,
    [property: JsonPropertyName("erpCompany")] string? ErpCompany,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("tradeName")] string? TradeName,
    [property: JsonPropertyName("gstNumber")] string? GstNumber,
    [property: JsonPropertyName("panNumber")] string? PanNumber,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("paymentTermCode")] string? PaymentTermCode,
    [property: JsonPropertyName("deliveryTermCode")] string? DeliveryTermCode,
    [property: JsonPropertyName("poResponseMode")] string PoResponseMode,
    [property: JsonPropertyName("registrationStatus")] string RegistrationStatus,
    [property: JsonPropertyName("addresses")] IReadOnlyList<SupplierAddressInputDoc> Addresses,
    [property: JsonPropertyName("contacts")] IReadOnlyList<SupplierContactInputDoc> Contacts,
    [property: JsonPropertyName("bankDetails")] IReadOnlyList<SupplierBankInputDoc> BankDetails,
    [property: JsonPropertyName("licenses")] IReadOnlyList<SupplierLicenseInputDoc> Licenses);

public sealed record SupplierAddressInputDoc(
    [property: JsonPropertyName("addressType")] string? AddressType,
    [property: JsonPropertyName("line1")] string? Line1,
    [property: JsonPropertyName("line2")] string? Line2,
    [property: JsonPropertyName("area")] string? Area,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("pincode")] string? Pincode,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("erpCode")] string? ErpCode);

public sealed record SupplierContactInputDoc(
    [property: JsonPropertyName("contactName")] string? ContactName,
    [property: JsonPropertyName("designation")] string? Designation,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("addressId")] Guid? AddressId,
    [property: JsonPropertyName("erpCode")] string? ErpCode);

public sealed record SupplierBankInputDoc(
    [property: JsonPropertyName("bankName")] string? BankName,
    [property: JsonPropertyName("bankAddress")] string? BankAddress,
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("ifscCode")] string? IfscCode,
    [property: JsonPropertyName("swiftCode")] string? SwiftCode,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("erpCode")] string? ErpCode);

public sealed record SupplierLicenseInputDoc(
    [property: JsonPropertyName("licenseNumber")] string? LicenseNumber,
    [property: JsonPropertyName("licenseType")] string? LicenseType,
    [property: JsonPropertyName("remarks")] string? Remarks,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate,
    [property: JsonPropertyName("erpCode")] string? ErpCode);

/// <summary>
/// Supplier-change input document (portalEntity <c>SupplierChange</c>, transaction <c>SupplierChange</c>).
/// <see cref="Entities"/> carries the full intended end-state per erpCode-keyed entity the change touched
/// (mirrors the legacy builder). Fields not applicable to an entity type are null — the request expression
/// projects per <c>entityType</c>.
/// </summary>
public sealed record SupplierChangeInputDoc(
    [property: JsonPropertyName("changeRequestId")] Guid ChangeRequestId,
    [property: JsonPropertyName("supplierCode")] string SupplierCode,
    [property: JsonPropertyName("supplierErpCode")] string? SupplierErpCode,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("changeStatus")] string ChangeStatus,
    [property: JsonPropertyName("entities")] IReadOnlyList<SupplierChangeEntityInputDoc> Entities);

public sealed record SupplierChangeEntityInputDoc(
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("erpCode")] string? ErpCode,
    [property: JsonPropertyName("deleted")] bool? Deleted,
    // Supplier
    [property: JsonPropertyName("legalName")] string? LegalName,
    [property: JsonPropertyName("tradeName")] string? TradeName,
    [property: JsonPropertyName("gstNumber")] string? GstNumber,
    [property: JsonPropertyName("panNumber")] string? PanNumber,
    [property: JsonPropertyName("msmeRegNumber")] string? MsmeRegNumber,
    [property: JsonPropertyName("msmeCategory")] string? MsmeCategory,
    [property: JsonPropertyName("website")] string? Website,
    // Address
    [property: JsonPropertyName("addressType")] string? AddressType,
    [property: JsonPropertyName("addressLine1")] string? AddressLine1,
    [property: JsonPropertyName("addressLine2")] string? AddressLine2,
    [property: JsonPropertyName("area")] string? Area,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("pincode")] string? Pincode,
    [property: JsonPropertyName("country")] string? Country,
    // Contact
    [property: JsonPropertyName("contactName")] string? ContactName,
    [property: JsonPropertyName("designation")] string? Designation,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("isPrimary")] bool? IsPrimary,
    // Bank
    [property: JsonPropertyName("bankName")] string? BankName,
    [property: JsonPropertyName("bankAddress")] string? BankAddress,
    [property: JsonPropertyName("accountName")] string? AccountName,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("ifscCode")] string? IfscCode,
    [property: JsonPropertyName("swiftCode")] string? SwiftCode,
    // License
    [property: JsonPropertyName("licenseNumber")] string? LicenseNumber,
    [property: JsonPropertyName("licenseType")] string? LicenseType,
    [property: JsonPropertyName("remarks")] string? Remarks,
    [property: JsonPropertyName("issueDate")] string? IssueDate,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate);

/// <summary>PO-negotiation input document (portalEntity <c>PoNegotiation</c>, transaction <c>PoNegotiationApprove</c>).</summary>
public sealed record PoNegotiationInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("poNumber")] string PoNumber,
    [property: JsonPropertyName("negotiationId")] Guid NegotiationId,
    [property: JsonPropertyName("submittedAt")] string SubmittedAt,
    [property: JsonPropertyName("negotiationStatus")] string NegotiationStatus,
    [property: JsonPropertyName("lines")] IReadOnlyList<PoNegotiationLineInputDoc> Lines);

public sealed record PoNegotiationLineInputDoc(
    [property: JsonPropertyName("positionNo")] int PositionNo,
    [property: JsonPropertyName("sequenceNo")] int SequenceNo,
    [property: JsonPropertyName("itemCode")] string? ItemCode,
    [property: JsonPropertyName("originalQty")] decimal? OriginalQty,
    [property: JsonPropertyName("negotiatedQty")] decimal? NegotiatedQty,
    [property: JsonPropertyName("originalDeliveryDate")] string? OriginalDeliveryDate,
    [property: JsonPropertyName("negotiatedDeliveryDate")] string? NegotiatedDeliveryDate,
    [property: JsonPropertyName("originalPrice")] decimal? OriginalPrice,
    [property: JsonPropertyName("negotiatedPrice")] decimal? NegotiatedPrice);
