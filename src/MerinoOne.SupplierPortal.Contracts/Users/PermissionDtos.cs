namespace MerinoOne.SupplierPortal.Contracts.Users;

/// <summary>A human-assignable permission offered by the role-permission picker.</summary>
public record PermissionListItemDto(
    string Code,
    string Name,
    string Module,
    string? Description);

/// <summary>
/// Register a new GLOBAL permission code in the catalog. Platform-operator only. NOTE: a permission code
/// enforces nothing until a matching <c>[Authorize(Policy = ...)]</c> gate references it in code.
/// </summary>
public record CreatePermissionRequest(
    string Code,
    string Name,
    string? Module,
    string? Description);
