using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class SupplierInvite : AuditableEntity
{
    public string LegalName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string InvitedBy { get; set; } = string.Empty;
    public string? MobileNo { get; set; }
    public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public Guid? SupplierId { get; set; }

    /// <summary>Set when an admin cancels a pending invite. Mutually exclusive with <see cref="ConsumedAt"/>.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>UserCode of the admin who cancelled the invite.</summary>
    public string? CancelledBy { get; set; }

    /// <summary>Timestamp of the most recent admin-initiated resend. Throttle window: 60s.</summary>
    public DateTime? LastResentAt { get; set; }

    /// <summary>How many times the invite has been resent by an admin.</summary>
    public int ResendCount { get; set; }
}
