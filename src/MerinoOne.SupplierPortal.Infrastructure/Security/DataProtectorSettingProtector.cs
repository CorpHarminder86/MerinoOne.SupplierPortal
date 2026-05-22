using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.AspNetCore.DataProtection;

namespace MerinoOne.SupplierPortal.Infrastructure.Security;

/// <summary>
/// Wraps ASP.NET DataProtection with a stable purpose so the key-ring remains compatible
/// across deployments. The purpose string is intentionally specific — adding a new
/// encrypted setting elsewhere should pick its own purpose, not reuse this one.
/// </summary>
public class DataProtectorSettingProtector : ISettingProtector
{
    public const string Purpose = "MerinoOne.SystemSetting.EmailConfig.Password";

    private readonly IDataProtector _protector;

    public DataProtectorSettingProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plainText)
        => string.IsNullOrEmpty(plainText) ? string.Empty : _protector.Protect(plainText);

    public string Unprotect(string cipherText)
        => string.IsNullOrEmpty(cipherText) ? string.Empty : _protector.Unprotect(cipherText);
}
