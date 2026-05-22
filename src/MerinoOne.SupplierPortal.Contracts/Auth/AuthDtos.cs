namespace MerinoOne.SupplierPortal.Contracts.Auth;

/// <summary>
/// Login by email is the preferred path for portal users. For backwards-compat with
/// the seeded internal users (sadmin1, admin1, etc.) the same field will also accept
/// a bare userCode — the server treats input with '@' as email, otherwise userCode.
/// </summary>
public record LoginRequest(string Email, string Password);

public record LoginResponse(
    string Token,
    DateTime ExpiresAt,
    string UserCode,
    string FullName,
    string[] Roles,
    string[] Permissions,
    bool MustChangePassword);

public record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);
