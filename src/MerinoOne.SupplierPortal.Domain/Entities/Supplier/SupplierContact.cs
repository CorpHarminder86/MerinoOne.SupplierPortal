using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

public class SupplierContact : AuditableEntity
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string? Designation { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public bool IsPrimary { get; set; }

    // R4 (2026-06-22) — Module 1e: ERP handle, populated via the /inbound/erp-ack channel.
    public string? ErpCode { get; set; }

    // R4 (2026-06-23) — optional link to one of the supplier's addresses (the contact's site/location).
    // SetNull on address delete so removing an address never cascades the contact away.
    public Guid? AddressId { get; set; }
    public SupplierAddress? Address { get; set; }
}
