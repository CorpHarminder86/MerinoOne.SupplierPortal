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
    Guid DefaultSeccodeId,
    CompanyAccessDto[] CompanyAccess);

/// <summary>
/// A company the user has access to (a <c>UserCompanyMap</c> row), shown in the admin user-detail view.
/// <see cref="AllSuppliers"/> = true → a direct full-access grant (every supplier in the company, incl.
/// future ones, seccode-bypassed but scoped to that company); false → supplier-derived (data is
/// seccode-scoped to the user's mapped suppliers). The two flavors coexist per company.
/// </summary>
public record CompanyAccessDto(
    Guid TenantEntityId,
    string Code,
    string Name,
    bool AllSuppliers,
    bool IsDefault);

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

/// <summary>
/// Enhancement Round 2 / Feature A — grant a user direct FULL access to a company (every supplier in it,
/// incl. future ones). The company's tenant must equal the acting tenant.
/// </summary>
public record GrantCompanyRequest(Guid TenantEntityId);

/// <summary>
/// Enhancement Round 2 / Feature B — bulk set the user's supplier maps for ONE company (diff). All
/// <see cref="SupplierIds"/> must belong to the query-string company; an empty list removes every map
/// for that company only. <see cref="CanWrite"/> applies to all rows in the batch.
/// </summary>
public record SetSupplierMapsRequest(Guid[] SupplierIds, bool CanWrite);

public record SetPasswordRequest(string NewPassword);
