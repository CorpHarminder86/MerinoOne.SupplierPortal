using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Settings;

/// <summary>
/// Global system configuration row. Generic key/value bucket grouped by category.
/// Soft-deleted + audited. Not seccoded — these are app-wide config, not tenant data.
/// </summary>
public class SystemSetting : AuditableEntity
{
    public string Category { get; set; } = string.Empty;     // 'EmailConfig' | 'SupplierInvite' | ...
    public string SettingKey { get; set; } = string.Empty;   // e.g. 'Host', 'ExpiryDays'
    public string SettingValue { get; set; } = string.Empty; // raw; may be encrypted (e.g. Password)
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
