using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// OTP issued against a SupplierInvite. One row per send — resends create new rows
/// keyed back to the same SupplierInviteId. The OTP itself is never stored; only a hash.
/// </summary>
public class InviteOtp : AuditableEntity
{
    public Guid SupplierInviteId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public SupplierInvite Invite { get; set; } = null!;
}
