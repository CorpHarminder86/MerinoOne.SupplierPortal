using MerinoOne.SupplierPortal.Domain.Entities.Admin;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R5 (TSD R5 Addendum §4.3 / §6.1) — the POINT-IN-TIME ship-to snapshot captured onto a
/// <see cref="PurchaseOrder"/> at inbound resolution. Mapped as an EF OWNED VALUE OBJECT onto the eight
/// <c>shipTo*</c> columns of <c>proc.PurchaseOrder</c> (not eight loose properties on the header).
///
/// The PO header displays THIS snapshot — written once at resolution and NOT refreshed by later edits to the
/// underlying <see cref="CompanyAddress"/> (consistent with how an ERP freezes ship-to onto an order). The
/// retained <c>PurchaseOrder.ShipToAddressId</c> FK gives live linkage back to the address; this VO is what
/// the PO renders and reports against.
///
/// Encapsulating the eight columns here means later phases get ONE copy mapper (<see cref="From"/>) — the
/// single dedup point for the inbound-resolve copy — instead of eight scattered assignments.
/// </summary>
public class ShipToSnapshot
{
    public string? AddressName { get; set; }
    public string? ErpCode { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Pincode { get; set; }
    public string? Country { get; set; }

    /// <summary>
    /// Single dedup point for the inbound-resolve copy (§6.2). Phase 2 / the inbound PO handler calls this to
    /// snapshot a resolved <see cref="CompanyAddress"/> onto the PO header — replacing what would otherwise be
    /// eight scattered field assignments.
    /// </summary>
    public static ShipToSnapshot From(CompanyAddress a) => new()
    {
        AddressName = a.AddressName,
        ErpCode = a.ErpCode,
        Line1 = a.AddressLine1,
        Line2 = a.AddressLine2,
        City = a.City,
        State = a.State,
        Pincode = a.Pincode,
        Country = a.Country,
    };
}
