using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

public class InforSyncLog : AuditableEntity, ITenantOwned
{
    /// <summary>Owning tenant — sync history is tenant-scoped.</summary>
    public Guid? TenantId { get; set; }

    public string EntityName { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public SyncStatus Status { get; set; }
    public string? PayloadRef { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
    public string? ErrorMessage { get; set; }

    /// <summary>Single-entity outbound id, or the batch's term-code list (comma-joined, capped ~400 chars).</summary>
    public string? EntityId { get; set; }

    /// <summary>Number of rows in the batch this log row represents (inbound writes one row per batch).</summary>
    public int EntityCount { get; set; }

    /// <summary>Full request JSON for the batch (capped to protect the SQL-Express 10 GB cap). Null when not stored.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Retry attempts recorded for this sync.</summary>
    public int RetryCount { get; set; }
}
