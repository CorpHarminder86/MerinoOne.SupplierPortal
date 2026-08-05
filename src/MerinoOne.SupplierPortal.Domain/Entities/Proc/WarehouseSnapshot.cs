using MerinoOne.SupplierPortal.Domain.Entities.Admin;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R13 (2026-08-05) — the POINT-IN-TIME warehouse (receiving-address) snapshot captured at inbound resolution.
/// Warehouse fully replaces the retired R5 ship-to: the inbound PO carries a per-LINE warehouse CODE that
/// resolves to a <see cref="CompanyAddress"/> (by <c>ErpCode</c>, non-base, active) within the PO's company;
/// on resolution this VO is frozen onto the owning row.
///
/// Mapped as an EF OWNED VALUE OBJECT onto eight <c>wh*</c> columns of the owner table — on
/// <c>proc.PurchaseOrderLine</c> (per line, since a PO may span warehouses) and on <c>proc.Asn</c> (the ASN's
/// single resolved warehouse). The row displays THIS snapshot; the retained <c>WarehouseAddressId</c> FK gives
/// live linkage back to the address. Written once at resolution and NOT refreshed by later CompanyAddress edits
/// (an ERP freezes the receiving address onto the order), exactly as the old ship-to snapshot behaved.
///
/// One copy mapper (<see cref="From"/>) is the single dedup point for the resolve-copy.
/// </summary>
public class WarehouseSnapshot
{
    public string? AddressName { get; set; }
    public string? ErpCode { get; set; }
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Pincode { get; set; }
    public string? Country { get; set; }

    /// <summary>Single dedup point for the resolve-copy: the inbound PO handler (and ASN create/update) call this
    /// to snapshot a resolved <see cref="CompanyAddress"/> onto the owning row — one call instead of eight
    /// scattered assignments.</summary>
    public static WarehouseSnapshot From(CompanyAddress a) => new()
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
