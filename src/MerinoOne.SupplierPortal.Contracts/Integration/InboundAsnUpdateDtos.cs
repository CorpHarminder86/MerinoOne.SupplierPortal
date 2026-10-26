using System.Text.Json.Serialization;

namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R17 (2026-10-26) — inbound ASN receipt update (POST /inbound/asn-update, X-APIKey + Integration.Inbound.AsnUpdate).
// Infor LN pushes the shipment status of an ASN the portal already posted, plus the per-line quantity it received.
// The reverse leg of the R16 ASN_Update post: the portal sends the shipment, LN reports back what arrived.

/// <summary>
/// One received line reported by LN for an ASN. Match key: <see cref="PositionNo"/> — the PO line position that the
/// portal's <c>AsnLine</c> snapshotted when the ASN was built. <see cref="PoNumber"/> is OPTIONAL and only
/// disambiguates when one ASN spans several POs that reuse the same position number.
/// <see cref="ReceivedQty"/> and <see cref="ActualDeliveryDate"/> are the ERP-owned receipt facts stored on the line.
/// </summary>
public record AsnUpdateLineRecord(
    int PositionNo,
    decimal? ReceivedQty,
    DateTime? ActualDeliveryDate,
    string? PoNumber = null);

/// <summary>
/// One ASN update. Match keys: <see cref="ErpSupplierCode"/> identifies the supplier and <see cref="SupplierAsn"/>
/// (JSON <c>supplierASN</c>) is the supplier-side ASN reference — the portal's ASN number — within that supplier.
/// <see cref="Shipment"/> is LN's own shipment number for the ASN. <see cref="Status"/> is LN's shipment status,
/// kept verbatim as <c>Asn.ErpShipmentStatus</c>. <see cref="Lines"/> carries the per-line receipt facts and may be
/// omitted for a status-only update.
/// </summary>
public record AsnUpdateRecord(
    string ErpSupplierCode,
    [property: JsonPropertyName("supplierASN")] string SupplierAsn,
    string Shipment,
    string Status,
    IReadOnlyList<AsnUpdateLineRecord>? Lines);

/// <summary>
/// Inbound ASN-update push body. <see cref="CompanyCode"/> is the Infor LN logistic company code; it is resolved to a
/// TenantEntity within the key's tenant. The ASN array is serialized under the JSON key <c>ASN</c>, as LN sends it.
/// See <see cref="PushGrnStatusRequest"/> for the company semantics shared by the transactional inbound endpoints.
/// </summary>
public record PushAsnUpdatesRequest(
    string CompanyCode,
    [property: JsonPropertyName("ASN")] IReadOnlyList<AsnUpdateRecord> Asn);
