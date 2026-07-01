namespace MerinoOne.SupplierPortal.Contracts.Masters;

// R5 (TSD R5 Addendum §4.2 / §5 — Component 1 / [[r5-consolidation]]). CONFIG-MASTER DTOs for the named,
// ERP-mappable ship-to addresses (admin.CompanyAddress) that hang off a company = admin.TenantEntity (the
// duplicate admin.Company was dropped; the company itself is served by Contracts.Companies.CompanyDto over
// TenantEntity). The CompanyId field below is that company's id — a TenantEntity id.

/// <summary>A named, ERP-mappable ship-to address under a company (§4.2). CompanyId = the owning TenantEntity id.</summary>
public record CompanyAddressDto(
    Guid Id,
    int Seq,
    Guid CompanyId,
    string AddressName,
    string? ErpCode,
    string AddressType,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string? Pincode,
    string Country,
    bool IsActive,
    DateTime CreatedOn);

/// <summary>Settings: create a ship-to address under a Company. AddressName required; ErpCode optional (unique per company).</summary>
public record CreateCompanyAddressRequest(
    Guid CompanyId,
    string AddressName,
    string? ErpCode,
    string AddressType,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string? Pincode,
    string? Country);

/// <summary>Settings: edit a ship-to address (deactivate via IsActive=false). ErpCode stays unique per company when present.</summary>
public record UpdateCompanyAddressRequest(
    string AddressName,
    string? ErpCode,
    string AddressType,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string? Pincode,
    string? Country,
    bool IsActive);
