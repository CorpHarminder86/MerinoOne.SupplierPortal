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
    bool MustChangePassword,
    bool RequiresMfa = false,
    string? MfaToken = null);

public record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Body posted to <c>/api/auth/mfa/verify</c> to complete login for an MFA-enabled
/// user. <c>MfaToken</c> is the opaque handle returned by the password leg.
/// </summary>
public record MfaVerifyRequest(string MfaToken, string Code);
