namespace MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

/// <summary>
/// Enhancement R4 — Module 9 (PO Schedule Calendar). A single calendar event: one PO's lines that share a
/// delivery date, grouped PO-wise per date so the UI can place a chip on each (Date, PO) cell.
/// </summary>
/// <param name="Date">The PurchaseOrderLine.DeliveryDate the chip sits on.</param>
/// <param name="PoNumber">PurchaseOrder.PoNumber.</param>
/// <param name="PurchaseOrderId">PurchaseOrder.Id (click-through to the PO detail).</param>
/// <param name="SupplierName">Supplier.LegalName.</param>
/// <param name="Items">The PO lines (item + qty) delivering on that date.</param>
public record PoCalendarEventDto(
    DateTime Date,
    string PoNumber,
    Guid PurchaseOrderId,
    string SupplierName,
    IReadOnlyList<PoCalendarItemDto> Items);

/// <param name="ItemCode">PurchaseOrderLine.ItemCode.</param>
/// <param name="Qty">PurchaseOrderLine.OrderQty.</param>
public record PoCalendarItemDto(
    string ItemCode,
    decimal Qty);
