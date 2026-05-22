using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

public class SupplierInvite : AuditableEntity
{
    public string LegalName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string InvitedBy { get; set; } = string.Empty;
    public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public Guid? SupplierId { get; set; }
}
