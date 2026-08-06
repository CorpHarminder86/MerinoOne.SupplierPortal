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
    // R11 (2026-07-28) — asn-v2: company/supplier/warehouse/date/shipment-ref header fields, per-line PO
    // reference + UoM, serials as objects. R11.2 (2026-07-29) — asn-v3: erpCompany/erpTransactionType/
    // erpDocumentNo removed (columns dropped in migration 0055). Neither v2 nor v3 has shipped to a tenant,
    // but each bump makes any pinned SampleDocumentJson stale — an ATTESTATION BLOCKER for a Dynamic AsnPost
    // config until re-pinned in the Endpoints UI.
    public const string Asn = "asn-v3";
    // R12 (2026-07-29) — purchaseOrder-v2: companyCode + headerDeliveryDate + the full PO line set, for the
    // LN PO_Update contract. D24: EVERY non-deleted line is carried with a nullable deliveryDate so the
    // line filter (D6) and the header-node handling (D7) live in the JSONata, not here — both are still
    // open against LN (probes P5a/P8) and this keeps their resolution a config edit.
    // 2026-08-06 — purchaseOrder-v3: each line gains orderQty + orderUnit (the shared line shape changed for
    // the negotiation payload's Quantity/UOM mapping; accept/reject expressions simply ignore the new fields).
    public const string PurchaseOrder = "purchaseOrder-v3";
    // R11.3 (2026-07-29) — supplier-v2: the never-written erpCompany field removed with the Supplier.ErpCompany
    // column (migration 0056).
    public const string Supplier = "supplier-v2";
    public const string SupplierChange = "supplierChange-v1";
    // R12 (2026-07-29) — poNegotiation-v2: same three additions as purchaseOrder-v2. NOTE the rename —
    // `lines` now means PO lines (identical shape to the PO document); the negotiation delta rows moved to
    // `negotiationLines`.
    // 2026-08-06 — poNegotiation-v3: lines gain orderQty (negotiated qty overlaid, D10-style) + orderUnit so
    // the negotiation request maps Quantity/UOM. The three request expressions are no longer the same text —
    // only the negotiation one maps the new fields.
    public const string PoNegotiation = "poNegotiation-v3";

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

/// <summary>ASN input document (portalEntity <c>Asn</c>, transaction <c>AsnPost</c>). Contract <c>asn-v2</c>.</summary>
public sealed record AsnInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("asnNumber")] string AsnNumber,
    // R11 — companyCode is the ERP company code (TenantEntity.Code, the same value LN sends inbound as
    // companyCode); supplierBp is Supplier.ErpCode, the LN business-partner id used to match inbound POs.
    [property: JsonPropertyName("companyCode")] string? CompanyCode,
    [property: JsonPropertyName("supplierBp")] string? SupplierBp,
    [property: JsonPropertyName("expectedDeliveryDate")] string ExpectedDeliveryDate,
    [property: JsonPropertyName("timeWindow")] string? TimeWindow,
    [property: JsonPropertyName("carrierName")] string? CarrierName,
    [property: JsonPropertyName("trackingNumber")] string? TrackingNumber,
    [property: JsonPropertyName("vehicleNumber")] string? VehicleNumber,
    [property: JsonPropertyName("driverName")] string? DriverName,
    [property: JsonPropertyName("driverPhone")] string? DriverPhone,
    // R11 — the D4 grouping key, snapshotted from the covered PO header.
    [property: JsonPropertyName("warehouse")] string? Warehouse,
    // R11 — createDate is the ASN's audit creation instant; shipmentDate is submittedAt, stamped at BUYER
    // APPROVAL (AsnSubmitExecutor, sharing AsnApproval.DecisionOn's timestamp) and therefore null on any ASN
    // that has not been approved. Both pre-formatted "o" so request expressions stay pure projection.
    [property: JsonPropertyName("createDate")] string? CreateDate,
    [property: JsonPropertyName("shipmentDate")] string? ShipmentDate,
    // R11 — supplier-entered shipment references (mandatory at Send-For-Approval, so non-null in practice
    // for anything that reaches the outbox).
    [property: JsonPropertyName("invoiceNo")] string? InvoiceNo,
    [property: JsonPropertyName("billOfLading")] string? BillOfLading,
    [property: JsonPropertyName("packingList")] string? PackingList,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("asnStatus")] string AsnStatus,
    // R11.2 — erpCompany/erpTransactionType/erpDocumentNo were dropped from the ASN (migration 0055; the
    // Invoice input doc keeps its trio). erpCode — the LN ASNNo ack write-back — is the correlation field.
    [property: JsonPropertyName("erpCode")] string? ErpCode,
    [property: JsonPropertyName("lines")] IReadOnlyList<AsnLineInputDoc> Lines);

public sealed record AsnLineInputDoc(
    // R11 — the line's source PO reference. Closes the gap recorded in the Infor Live cutover checklist:
    // a multi-PO ASN was previously unrepresentable to LN because no line carried its PO.
    [property: JsonPropertyName("poNumber")] string? PoNumber,
    [property: JsonPropertyName("poOrigin")] string? PoOrigin,
    [property: JsonPropertyName("positionNo")] int? PositionNo,
    [property: JsonPropertyName("sequenceNo")] int? SequenceNo,
    [property: JsonPropertyName("itemCode")] string? ItemCode,
    [property: JsonPropertyName("shippedQty")] decimal ShippedQty,
    // R11 (D5) — the inventory-unit quantity. A PURE MIRROR of shippedQty: the portal has no UoM conversion
    // (Unit.ConversionFactor exists but is used nowhere, and PurchaseOrderLine.OrderUnit is an unFK'd free
    // string), so there is nothing to convert from. Do NOT "fix" this into a conversion — the name asserts
    // one, but the agreed contract is equality.
    [property: JsonPropertyName("shippedQtyInvUnit")] decimal ShippedQtyInvUnit,
    // R11 — the PO line's order unit, carried through verbatim.
    [property: JsonPropertyName("uom")] string? Uom,
    [property: JsonPropertyName("batchNumber")] string? BatchNumber,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate,
    // Null (not empty) when the line carries no per-unit capture — request expressions reproduce the
    // legacy WhenWritingNull omission by navigating the missing value.
    // R11 — serials are OBJECTS now (serial + its own expiry), not bare strings.
    [property: JsonPropertyName("serials")] IReadOnlyList<AsnSerialInputDoc>? Serials,
    [property: JsonPropertyName("lots")] IReadOnlyList<AsnLotInputDoc>? Lots);

/// <summary>R11 — one captured serial. Previously a bare string in the input doc and on the wire.</summary>
public sealed record AsnSerialInputDoc(
    [property: JsonPropertyName("serial")] string Serial,
    [property: JsonPropertyName("expiry")] string? Expiry);

public sealed record AsnLotInputDoc(
    [property: JsonPropertyName("lotNo")] string LotNo,
    [property: JsonPropertyName("qty")] decimal Qty,
    [property: JsonPropertyName("expiryDate")] string? ExpiryDate);

/// <summary>
/// Purchase-order input document — shared by <c>PoAccept</c> and <c>PoReject</c> (R12/D14 retired the
/// PoAcknowledge outbound push). <see cref="ResponseContext"/> folds the per-row parameters historically
/// carried on <c>OutboxMessage.PayloadJson</c> (proposed date, reject reason) into the document so gate +
/// request read one root (D-R9-7).
///
/// <para><b>R12 / D24 — why the whole line set is here.</b> <see cref="Lines"/> carries EVERY non-deleted
/// line with a nullable <c>deliveryDate</c>, and <see cref="HeaderDeliveryDate"/> carries the precomputed
/// all-lines-agree value (null when they disagree or any is missing). Which lines actually reach the wire
/// (D6) and how "no common date" is expressed (D7) are therefore decisions the JSONata makes — both are
/// still unconfirmed against LN, and this keeps their resolution a config edit rather than a code change.
/// The agree/disagree TEST stays here on purpose: JSONata null-comparison over a nullable collection is a
/// footgun, and C# is where the dates are already materialised.</para>
/// </summary>
public sealed record PurchaseOrderInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("poNumber")] string PoNumber,
    [property: JsonPropertyName("poStatus")] string PoStatus,
    [property: JsonPropertyName("erpStatus")] string? ErpStatus,
    [property: JsonPropertyName("acknowledgmentAt")] string? AcknowledgmentAt,
    [property: JsonPropertyName("acceptedAt")] string? AcceptedAt,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason,
    /// <summary>R12 — LN logistic company (<c>TenantEntity.Code</c>) for THIS PO. Null ⇒ the dispatch fails
    /// permanently (D9): PO_Update routes on the body company, so there is no header fallback to save it.</summary>
    [property: JsonPropertyName("companyCode")] string? CompanyCode,
    /// <summary>R12 — the delivery date every line agrees on, ISO-8601 UTC with <c>Z</c>; null when the lines
    /// disagree or any line has none (D7).</summary>
    [property: JsonPropertyName("headerDeliveryDate")] string? HeaderDeliveryDate,
    [property: JsonPropertyName("lines")] IReadOnlyList<PurchaseOrderLineInputDoc> Lines,
    [property: JsonPropertyName("responseContext")] PoResponseContextInputDoc ResponseContext);

/// <summary>
/// R12 — one PO line as the PO_Update contract sees it. <c>deliveryDate</c> is the EFFECTIVE date: the PO
/// line's own for accept/reject, or the negotiated one where an approved negotiation changed it (D10).
/// Null is preserved rather than filtered so the expression owns the D6 decision.
/// <para>v3 — <c>orderQty</c> is the EFFECTIVE quantity under the same D10 rule (the negotiation's
/// <c>negotiatedQty</c> where an approved negotiation set one, else the PO line's own <c>OrderQty</c>);
/// <c>orderUnit</c> is the PO line's unit, echoed back exactly as it arrived inbound from LN.</para>
/// </summary>
public sealed record PurchaseOrderLineInputDoc(
    [property: JsonPropertyName("positionNo")] int PositionNo,
    [property: JsonPropertyName("sequenceNo")] int SequenceNo,
    [property: JsonPropertyName("deliveryDate")] string? DeliveryDate,
    [property: JsonPropertyName("orderQty")] decimal OrderQty,
    [property: JsonPropertyName("orderUnit")] string? OrderUnit);

public sealed record PoResponseContextInputDoc(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("proposedDeliveryDate")] string? ProposedDeliveryDate,
    /// <summary>Reject reason. NOT on the wire — <c>PurchaseOrderDetail</c> has no field for it (R12 §5).
    /// Retained so that becomes a pure mapping edit the day LN supplies a placeholder.</summary>
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>Supplier master input document (portalEntity <c>Supplier</c>, transaction <c>SupplierSync</c>).</summary>
public sealed record SupplierInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("supplierCode")] string SupplierCode,
    [property: JsonPropertyName("erpCode")] string? ErpCode,
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

/// <summary>
/// PO-negotiation input document (portalEntity <c>PoNegotiation</c>, transaction <c>PoNegotiationApprove</c>).
///
/// <para><b>R12 — `lines` was renamed.</b> It now carries the PO line set in the SAME shape as
/// <see cref="PurchaseOrderInputDoc.Lines"/>, so all three PO_Update request expressions are identical text
/// bar the <c>POStatus</c> literal. The negotiation delta rows (original vs negotiated qty/price/date) moved
/// to <see cref="NegotiationLines"/> — still exposed, because a gate or a future mapping may want them, and
/// because they are the audit trail for what <see cref="Lines"/>' effective dates were derived from.</para>
///
/// <para>D10: a line the approved negotiation changed carries its <c>negotiatedDeliveryDate</c>; every other
/// line keeps the PO line's own. Local <c>PurchaseOrderLine</c> rows are NOT mutated on approve — the R4
/// decision stands — so this document is the only place the agreed dates are assembled.</para>
/// </summary>
public sealed record PoNegotiationInputDoc(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("poNumber")] string PoNumber,
    [property: JsonPropertyName("negotiationId")] Guid NegotiationId,
    [property: JsonPropertyName("submittedAt")] string SubmittedAt,
    [property: JsonPropertyName("negotiationStatus")] string NegotiationStatus,
    /// <summary>R12 — LN logistic company for the negotiation's PO. Resolved via the PO, never the
    /// negotiation, so the body company and the <c>X-Infor-LnCompany</c> header cannot disagree.</summary>
    [property: JsonPropertyName("companyCode")] string? CompanyCode,
    /// <summary>R12 — the effective delivery date every PO line agrees on after the D10 overlay; null when
    /// they disagree or any is missing (D7).</summary>
    [property: JsonPropertyName("headerDeliveryDate")] string? HeaderDeliveryDate,
    [property: JsonPropertyName("lines")] IReadOnlyList<PurchaseOrderLineInputDoc> Lines,
    [property: JsonPropertyName("negotiationLines")] IReadOnlyList<PoNegotiationLineInputDoc> NegotiationLines);

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
