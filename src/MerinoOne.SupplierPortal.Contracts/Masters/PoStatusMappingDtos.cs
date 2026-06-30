namespace MerinoOne.SupplierPortal.Contracts.Masters;

// R5 (TSD R5 Addendum §4.7 / §11 — Component 7, ERP→portal PO status mapping). Config-master DTOs for the
// Settings mapping editor. Many ERP statuses may map to ONE portal status; each erpStatus is unique per tenant.
// The target poStatus is restricted to the ERP-driven subset (Draft | Released | Cancelled | Closed | Delivered,
// §11.2) — the supplier/fulfilment-driven statuses are not offered.

/// <summary>An ERP→portal PO-status mapping row (§4.7).</summary>
public record PoStatusMappingDto(
    Guid Id,
    int Seq,
    string ErpStatus,
    string PoStatus,
    bool IsActive,
    DateTime CreatedOn);

/// <summary>Settings: create a mapping row. ErpStatus unique per tenant; PoStatus must be an ERP-driven target.</summary>
public record CreatePoStatusMappingRequest(string ErpStatus, string PoStatus);

/// <summary>Settings: edit a mapping row (re-target the portal status and/or deactivate via IsActive=false).</summary>
public record UpdatePoStatusMappingRequest(string PoStatus, bool IsActive);
