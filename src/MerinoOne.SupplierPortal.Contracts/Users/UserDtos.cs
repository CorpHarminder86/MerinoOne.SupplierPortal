namespace MerinoOne.SupplierPortal.Contracts.Users;

public record UserListItemDto(
    Guid Id,
    int Seq,
    string UserCode,
    string FullName,
    string Email,
    bool IsInternal,
    bool IsMfaEnabled,
    bool IsActive,
    string[] Roles,
    int SupplierMapCount,
    DateTime CreatedOn);

public record UserDetailDto(
    Guid Id,
    int Seq,
    string UserCode,
    string FullName,
    string Email,
    bool IsInternal,
    bool IsMfaEnabled,
    bool IsActive,
    string[] Roles,
    int SupplierMapCount,
    DateTime CreatedOn,
    Guid[] SupplierIds,
    MappedSupplierDto[] MappedSuppliers,
    Guid DefaultSeccodeId);

/// <summary>
/// A supplier a user is mapped to, resolved cross-company for the admin user-detail view. The CompanyCode
/// is the supplier's company — shown so an admin sees the mapping's company, and ALL mappings appear
/// regardless of the header's active-company selection (user↔supplier mapping is tenant-wide admin config).
/// </summary>
public record MappedSupplierDto(
    Guid SupplierId,
    string SupplierCode,
    string LegalName,
    string CompanyCode,
    bool CanWrite);

public record CreateUserRequest(
    string UserCode,
    string FullName,
    string Email,
    string Password,
    bool IsInternal,
    bool IsMfaEnabled,
    string[] Roles);

public record UpdateUserRequest(
    string FullName,
    string Email,
    bool IsInternal,
    bool IsMfaEnabled);

public record AssignRoleRequest(Guid RoleId);

/// <summary>
/// Map a user to a supplier. <see cref="TenantEntityId"/> is the company the mapping is made under —
/// the supplier must belong to that company (closes "supplier spanning companies"), the company's tenant
/// must equal the acting tenant, and the user is auto-granted company access if missing.
/// </summary>
public record MapSupplierRequest(Guid SupplierId, bool CanWrite, Guid TenantEntityId);

public record SetPasswordRequest(string NewPassword);
