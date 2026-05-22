namespace MerinoOne.SupplierPortal.Application.Common.Security;

/// <summary>
/// Symmetric protector for sensitive system-settings values (currently only
/// <c>EmailConfig.Password</c>). Infrastructure binds this to ASP.NET DataProtection
/// with a stable purpose so the key-ring rotates cleanly across deployments.
/// </summary>
public interface ISettingProtector
{
    string Protect(string plainText);
    string Unprotect(string cipherText);
}
