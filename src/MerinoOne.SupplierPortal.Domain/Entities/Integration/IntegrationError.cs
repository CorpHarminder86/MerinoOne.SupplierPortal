using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

public class IntegrationError : AuditableEntity, ITenantOwned
{
    /// <summary>Owning tenant — integration errors are tenant-scoped.</summary>
    public Guid? TenantId { get; set; }

    public Guid? SyncLogId { get; set; }
    public InforSyncLog? SyncLog { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastRetriedAt { get; set; }
    public bool IsResolved { get; set; }
    public string? ResolutionNote { get; set; }
}
