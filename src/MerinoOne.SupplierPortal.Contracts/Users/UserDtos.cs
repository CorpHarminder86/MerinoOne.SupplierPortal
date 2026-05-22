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
    Guid DefaultSeccodeId);

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

public record MapSupplierRequest(Guid SupplierId, bool CanWrite);

public record SetPasswordRequest(string NewPassword);
