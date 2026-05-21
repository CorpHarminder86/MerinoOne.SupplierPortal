using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

public class InforEndpointMap : AuditableEntity
{
    public string EntityName { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public string InforEndpointUrl { get; set; } = string.Empty;
    public string? BodName { get; set; }
    public bool IsEnabled { get; set; } = true;
}
