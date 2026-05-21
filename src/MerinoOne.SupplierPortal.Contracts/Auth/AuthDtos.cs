namespace MerinoOne.SupplierPortal.Contracts.Auth;

public record LoginRequest(string UserCode, string Password);
public record LoginResponse(string Token, DateTime ExpiresAt, string UserCode, string FullName, string[] Roles, string[] Permissions);
