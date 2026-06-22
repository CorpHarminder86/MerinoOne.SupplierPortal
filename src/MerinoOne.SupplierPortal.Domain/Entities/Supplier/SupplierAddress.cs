using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

public class SupplierAddress : AuditableEntity
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string AddressType { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    // Optional locality/area, typically auto-populated from the selected PostalCode master.
    public string? Area { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Pincode { get; set; } = string.Empty;
    public string Country { get; set; } = "India";

    // Optional geo-master links (tenant-scoped masters; resolved via autocomplete). The string fields
    // above are kept as a denormalized snapshot so an international / free-typed address still persists.
    public Guid? CountryId { get; set; }
    public Country? CountryRef { get; set; }
    public Guid? StateId { get; set; }
    public State? StateRef { get; set; }
    public Guid? CityId { get; set; }
    public City? CityRef { get; set; }
    public Guid? PostalCodeId { get; set; }
    public PostalCode? PostalCodeRef { get; set; }

    // R4 (2026-06-22) — Module 1e: ERP handle, populated via the /inbound/erp-ack channel.
    public string? ErpCode { get; set; }
}
