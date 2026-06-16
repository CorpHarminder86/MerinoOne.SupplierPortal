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

    /// <summary>
    /// Cross-tenant onboarding actor. A Platform Admin bypasses the tenant filter and holds NO
    /// business-data permissions, so the Platform nav group and the tenant-context indicator
    /// (rather than the company selector) are gated on this flag.
    /// </summary>
    public bool IsPlatformAdmin => Roles.Contains("PlatformAdmin");

    public bool HasPermission(string code) => Permissions.Contains(code);

    /// <summary>
    /// The tenant id carried in the JWT "tenant" claim, decoded client-side for the whoami diagnostic.
    /// Null for a Platform Admin (no tenant) or when the token has no tenant claim.
    /// </summary>
    public Guid? TenantId => ReadClaim("tenant") is { } v && Guid.TryParse(v, out var g) ? g : null;

    /// <summary>The raw "company" claim values (one per accessible company, or "ALL" for a Tenant Admin).</summary>
    public string[] CompanyClaims => ReadClaims("company");

    private string? ReadClaim(string type) => ReadClaims(type).FirstOrDefault();

    /// <summary>Best-effort decode of the JWT payload for a multi-valued claim. Never throws.</summary>
    private string[] ReadClaims(string type)
    {
        if (string.IsNullOrEmpty(Token)) return Array.Empty<string>();
        try
        {
            var parts = Token.Split('.');
            if (parts.Length < 2) return Array.Empty<string>();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(type, out var el)) return Array.Empty<string>();
            if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
                return el.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray();
            var single = el.GetString();
            return string.IsNullOrEmpty(single) ? Array.Empty<string>() : new[] { single };
        }
        catch { return Array.Empty<string>(); }
    }

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
