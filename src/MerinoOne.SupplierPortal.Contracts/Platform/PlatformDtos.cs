namespace MerinoOne.SupplierPortal.Contracts.Platform;

/// <summary>Create a new tenant (cross-tenant — Platform Admin only).</summary>
public record CreateTenantRequest(string Name);

/// <summary>Create a physical company (TenantEntity / Infor LN logistic company) under a tenant.</summary>
public record CreateTenantEntityRequest(Guid TenantId, string Code, string Name);

/// <summary>
/// Create the tenant's first Tenant Admin user. The temporary password is generated server-side
/// (returned once) and the user is forced to change it on first login.
/// </summary>
public record CreateTenantAdminRequest(
    Guid TenantId,
    string UserCode,
    string FullName,
    string Email);

public record TenantDto(Guid Id, int Seq, string Name, bool IsActive, int CompanyCount, DateTime CreatedOn);

public record TenantEntityDto(Guid Id, int Seq, Guid TenantId, string Code, string Name, bool IsActive, DateTime CreatedOn);

/// <summary>Result of onboarding a tenant admin. The temporary password is shown ONCE.</summary>
public record CreateTenantAdminResultDto(Guid UserId, string UserCode, string Email, string TemporaryPassword);
