using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

public class InforSyncLog : AuditableEntity
{
    public string EntityName { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public SyncStatus Status { get; set; }
    public string? PayloadRef { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }
}
