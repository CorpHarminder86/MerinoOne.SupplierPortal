using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// Admin-editable email template row, one per email kind (Invite, InviteOtp, Welcome,
/// PasswordChanged, LoginOtp). Loaded at send-time by <see cref="TemplateKey"/>. Subject
/// and HTML body are editable; notes is a freetext placeholder hint for admins.
/// Admin-global setting (no seccode, no rowversion, no FK to user/tenant).
/// </summary>
public class EmailTemplate : AuditableEntity
{
    public string TemplateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}
