using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

public class InforEndpointMap : AuditableEntity, ITenantOwned
{
    /// <summary>Owning tenant — endpoint config is tenant-scoped.</summary>
    public Guid? TenantId { get; set; }

    public string EntityName { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public string InforEndpointUrl { get; set; } = string.Empty;
    public string? BodName { get; set; }
    public bool IsEnabled { get; set; } = true;

    // Endpoint "session" — operational liveness telemetry for this inbound channel,
    // updated transactionally by the inbound upsert path (TenantCompany module §4).
    public DateTime? LastReceivedAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastIdempotencyKey { get; set; }
    public string? LastMessage { get; set; }
    public int ReceivedCount { get; set; }
}
