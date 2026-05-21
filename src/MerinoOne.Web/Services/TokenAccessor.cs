namespace MerinoOne.Web.Services;

public class TokenAccessor
{
    public string? Token { get; set; }
    public string? UserCode { get; set; }
    public string? FullName { get; set; }
    public string[] Roles { get; set; } = Array.Empty<string>();
    public string[] Permissions { get; set; } = Array.Empty<string>();
    public DateTime? ExpiresAt { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(Token) && (!ExpiresAt.HasValue || ExpiresAt.Value > DateTime.UtcNow);
    public bool IsAdmin => Roles.Contains("Admin") || Roles.Contains("SuperAdmin");
    public bool IsSupplier => Roles.Contains("Supplier");
    public bool HasPermission(string code) => Permissions.Contains(code);

    public void Clear()
    {
        Token = null; UserCode = null; FullName = null;
        Roles = Array.Empty<string>(); Permissions = Array.Empty<string>(); ExpiresAt = null;
    }
}
