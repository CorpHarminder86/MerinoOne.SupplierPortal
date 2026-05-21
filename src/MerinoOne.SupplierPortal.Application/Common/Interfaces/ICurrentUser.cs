namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

public interface ICurrentUser
{
    string UserCode { get; }
    string? UserName { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> Permissions { get; }
    bool IsAuthenticated { get; }
    bool IsManager { get; }
    bool IsAdmin { get; }
    bool HasPermission(string code);
}
