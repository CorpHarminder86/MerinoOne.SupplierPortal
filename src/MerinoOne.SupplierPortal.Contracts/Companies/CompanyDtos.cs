namespace MerinoOne.SupplierPortal.Contracts.Companies;

/// <summary>A physical company (TenantEntity) in the current tenant.</summary>
public record CompanyDto(Guid Id, int Seq, string Code, string Name, bool IsActive, DateTime CreatedOn);

/// <summary>Tenant-Admin: create a company in the current tenant.</summary>
public record CreateCompanyRequest(string Code, string Name);

/// <summary>Tenant-Admin: rename / re-code an existing company in the current tenant.</summary>
public record UpdateCompanyRequest(string Code, string Name);
