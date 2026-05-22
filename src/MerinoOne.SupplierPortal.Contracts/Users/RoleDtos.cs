namespace MerinoOne.SupplierPortal.Contracts.Users;

public record RoleListItemDto(
    Guid Id,
    int Seq,
    string Name,
    int PermissionCount,
    int UserCount);

public record RoleDetailDto(
    Guid Id,
    int Seq,
    string Name,
    string[] PermissionCodes);

public record CreateRoleRequest(string Name, string[] PermissionCodes);

public record UpdateRoleRequest(string Name);

public record AssignPermissionsRequest(string[] PermissionCodes);
