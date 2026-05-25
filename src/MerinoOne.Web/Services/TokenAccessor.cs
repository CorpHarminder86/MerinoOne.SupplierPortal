namespace MerinoOne.Web.Services;

public class TokenAccessor
{
    public string? Token { get; private set; }
    public string? UserCode { get; private set; }
    public string? Email { get; private set; }
    public string? FullName { get; private set; }
    public string[] Roles { get; private set; } = Array.Empty<string>();
    public string[] Permissions { get; private set; } = Array.Empty<string>();
    public DateTime? ExpiresAt { get; private set; }
    public bool MustChangePassword { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(Token) && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow);
    public bool IsAdmin => Roles.Contains("Admin") || Roles.Contains("SuperAdmin");
    public bool IsSupplier => Roles.Contains("Supplier");
    public bool HasPermission(string code) => Permissions.Contains(code);

    public event Action? Changed;
    public event Action? SessionExpired;

    public void NotifySessionExpired() => SessionExpired?.Invoke();

    public void SetSession(
        string token,
        string? userCode,
        string? email,
        string? fullName,
        string[] roles,
        string[] permissions,
        DateTime? expiresAt,
        bool mustChangePassword)
    {
        Token = token;
        UserCode = userCode;
        Email = email;
        FullName = fullName;
        Roles = roles ?? Array.Empty<string>();
        Permissions = permissions ?? Array.Empty<string>();
        ExpiresAt = expiresAt;
        MustChangePassword = mustChangePassword;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Token = null;
        UserCode = null;
        Email = null;
        FullName = null;
        Roles = Array.Empty<string>();
        Permissions = Array.Empty<string>();
        ExpiresAt = null;
        MustChangePassword = false;
        Changed?.Invoke();
    }
}
