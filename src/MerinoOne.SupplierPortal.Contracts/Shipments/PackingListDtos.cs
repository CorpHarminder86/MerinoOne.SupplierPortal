namespace MerinoOne.SupplierPortal.Contracts.Shipments;

/// <summary>
/// R18 (2026-11-03) — one party block on the Packing List (supplier, ship-from or ship-to): the name, the address as
/// ready-to-print lines, and the contact / tax identifiers shown beneath it. Null / empty values render as "-" in the
/// generator.
/// </summary>
public record PackingListPartyDto(
    string? Name,
    IReadOnlyList<string> AddressLines,
    string? Phone,
    string? Email,
    string? GstNo,
    string? Cin);

/// <summary>
/// R18 (2026-11-03) — one Packing List row: an ASN line with its PO reference, item, shipped quantity and the serial /
/// lot numbers that travel with it. <c>QtyDecimals</c> is the number of decimal places <c>ShippedQty</c> is printed
/// with. Null / empty values render as "-" in the generator.
/// </summary>
public record PackingListLineDto(
    int SlNo,
    string? PoNumber,
    int? PoLine,
    string? Position,
    int? Seq,
    string? ItemCode,
    string? ItemDescription,
    decimal ShippedQty,
    int QtyDecimals,
    string? Uom,
    IReadOnlyList<string> SerialLotNumbers,
    string? ExpiryDate);

/// <summary>
/// R18 (2026-11-03) — read model for an ASN's Packing List PDF: the three party blocks, the shipment header (ASN, POs,
/// carrier, vehicle, tracking, India e-way bill / IRN references), the line rows and the shipped-quantity total
/// (<c>TotalQtyDecimals</c> is the number of decimal places <c>TotalShippedQty</c> is printed with). Null / empty
/// values render as "-" in the generator.
/// </summary>
public record PackingListDto(
    PackingListPartyDto Supplier,
    PackingListPartyDto ShipFrom,
    PackingListPartyDto ShipTo,
    string? PackingListNumber,
    DateTime? PackingListDate,
    string AsnNumber,
    string? PurchaseOrders,
    string? ShipmentNumber,
    string? SupplierCode,
    string? Currency,
    string? DeliveryTerms,
    string? Carrier,
    string? VehicleNo,
    string? TrackingNo,
    string? EwayBillNo,
    DateOnly? EwayBillDate,
    string? IrnNo,
    DateOnly? IrnDate,
    IReadOnlyList<PackingListLineDto> Lines,
    decimal TotalShippedQty,
    int TotalQtyDecimals,
    string? Remarks,
    DateTime GeneratedAt);
