using MerinoOne.SupplierPortal.Contracts.Shipments;

namespace MerinoOne.SupplierPortal.Contracts.Invoices;

/// <summary>
/// R19 (2026-11-16) — one row of the supplier tax-invoice PDF: the PO reference, item, billed quantity (printed with
/// <c>QtyDecimals</c> places), unit price, the line discount (% informational, amount = total for the line), tax rate /
/// amount and the line total. Null / empty values render as "-" in the generator.
/// </summary>
public record SupplierInvoiceLineDto(
    int SlNo,
    string? PoNumber,
    int? PoLine,
    string? Position,
    int? Seq,
    string? ItemCode,
    string? ItemDescription,
    decimal Qty,
    int QtyDecimals,
    string? Uom,
    decimal UnitPrice,
    decimal? DiscountPct,
    decimal DiscountAmount,
    decimal? TaxRatePct,
    decimal TaxAmount,
    decimal LineTotal);

/// <summary>
/// R19 (2026-11-16) — read model for an invoice's supplier-format PDF: the four party blocks (supplier, bill-to, ship-to,
/// ship-from), the invoice / ASN / shipment header (carrier, vehicle, India e-way bill / IRN references), the line rows,
/// the totals and the amount in words. Null / empty values render as "-" in the generator.
/// </summary>
public record SupplierInvoiceReportDto(
    PackingListPartyDto Supplier,
    PackingListPartyDto BillTo,
    PackingListPartyDto ShipTo,
    PackingListPartyDto ShipFrom,
    string InvoiceNumber,
    DateTime InvoiceDate,
    string? PurchaseOrders,
    string? AsnNumber,
    string? ShipmentNumber,
    string? GrnNumber,
    string Currency,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? SupplierCode,
    string? SupplierGstNo,
    string? CustomerGstNo,
    DateTime? AsnDate,
    DateTime? ExpectedDelivery,
    string? Carrier,
    string? TrackingNo,
    string? BillOfLadingNo,
    string? VehicleNo,
    string? Driver,
    string? PackingListNo,
    string? EwayBillNo,
    DateOnly? EwayBillDate,
    string? IrnNo,
    DateOnly? IrnDate,
    IReadOnlyList<SupplierInvoiceLineDto> Lines,
    decimal TotalDiscount,
    decimal TotalTaxable,
    decimal TotalTax,
    decimal GrandTotal,
    string AmountInWords,
    string? Notes,
    DateTime GeneratedAt);

/// <summary>
/// R19 (2026-11-16) — one invoice of an ASN, for the ASN page's invoice picker (an ASN can carry several invoices).
/// </summary>
public record AsnInvoiceRefDto(Guid Id, string InvoiceNumber, string InvoiceStatus, DateTime InvoiceDate);

/// <summary>
/// 2026-11-17 — one pre-Post Invoice report of an ASN (one per currency / payment-term group, numbered like the draft
/// invoices Post will create), for the ASN page's report button / picker.
/// </summary>
public record AsnInvoiceReportRefDto(int Group, string InvoiceNumber, string Currency, string? PaymentTerms, decimal GrandTotal);
