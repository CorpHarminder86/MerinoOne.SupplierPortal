namespace MerinoOne.SupplierPortal.Contracts.Masters;

// R5 (TSD R5 Addendum §4.2 / §5 — Component 1 / [[r5-consolidation]]). CONFIG-MASTER DTOs for the named,
// ERP-mappable ship-to addresses (admin.CompanyAddress) that hang off a company = admin.TenantEntity (the
// duplicate admin.Company was dropped; the company itself is served by Contracts.Companies.CompanyDto over
// TenantEntity). The CompanyId field below is that company's id — a TenantEntity id.

/// <summary>A named, ERP-mappable address under a company (§4.2). CompanyId = the owning TenantEntity id. R13:
/// IsBase flags the company's single BASE address (no ErpCode); all others are the warehouse pool.</summary>
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
    bool IsBase,
    DateTime CreatedOn);

/// <summary>Settings: create an address under a Company. AddressName required. R13: IsBase=true → the company's
/// single base address (ErpCode forced null, type "Base"); IsBase=false → a warehouse-pool row (type "Warehouse",
/// ErpCode unique per company).</summary>
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
    string? Country,
    bool IsBase = false);

/// <summary>Settings: edit an address (deactivate via IsActive=false). R13: IsBase toggles base vs warehouse-pool
/// (base forces ErpCode null + type "Base" + singleton; warehouse keeps the per-company ErpCode uniqueness).</summary>
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
    bool IsActive,
    bool IsBase = false);
