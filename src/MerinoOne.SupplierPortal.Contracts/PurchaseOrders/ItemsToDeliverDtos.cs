namespace MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

/// <summary>
/// Enhancement R4 — Module 8 (Items-to-be-Delivered). Open PO lines grouped by (ItemCode, DeliveryDate).
/// </summary>
/// <param name="ItemCode">PurchaseOrderLine.ItemCode (the grouping key).</param>
/// <param name="ItemName">PurchaseOrderLine.ItemDescription (representative for the group).</param>
/// <param name="DeliveryDate">PurchaseOrderLine.DeliveryDate (the grouping key).</param>
/// <param name="TotalQty">Σ PurchaseOrderLine.OrderQty across the group.</param>
/// <param name="OpenQty">Σ (OrderQty − Σ GoodsReceipt.ReceivedQty) across the group's lines.</param>
/// <param name="PoCount">Distinct PurchaseOrder count contributing to the group.</param>
public record ItemsToDeliverRowDto(
    string ItemCode,
    string? ItemName,
    DateTime? DeliveryDate,
    decimal TotalQty,
    decimal OpenQty,
    int PoCount);
