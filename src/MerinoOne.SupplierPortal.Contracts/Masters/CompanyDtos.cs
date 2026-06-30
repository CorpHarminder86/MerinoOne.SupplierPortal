namespace MerinoOne.SupplierPortal.Contracts.Masters;

// R5 (TSD R5 Addendum §4.1–4.2 / §5 — Component 1, Company Master). These are the admin.Company / admin.CompanyAddress
// CONFIG-MASTER DTOs — the CUSTOMER (buying entity) the supplier ships to, plus its named, ERP-mappable ship-to
// addresses. NAMED *CompanyMaster* (not *Company*) deliberately: Contracts.Companies.CompanyDto already exists (the
// TenantEntity/active-company selector record) and both Contracts namespaces are imported together in the Blazor
// _Imports, so a bare CompanyDto here would be globally ambiguous. CompanyAddress* records do not collide.

/// <summary>A Company-master row — the customer (buying entity) keyed 1:1 to a tenantEntityId (the PO's company).</summary>
public record CompanyMasterDto(
    Guid Id,
    int Seq,
    Guid? TenantEntityId,
    string Name,
    bool IsActive,
    DateTime CreatedOn);

/// <summary>Settings: create a Company-master row for the active company (tenantEntityId is resolved from context).</summary>
public record CreateCompanyMasterRequest(string Name);

/// <summary>Settings: rename / activate-deactivate a Company-master row.</summary>
public record UpdateCompanyMasterRequest(string Name, bool IsActive);

/// <summary>A named, ERP-mappable ship-to address under a Company (§4.2).</summary>
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
