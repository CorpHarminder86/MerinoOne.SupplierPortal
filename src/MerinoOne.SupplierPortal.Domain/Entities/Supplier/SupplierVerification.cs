using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

public class SupplierVerification : BaseAggregateRoot
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public VerificationType VerificationType { get; set; }
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
    public string AttemptedBy { get; set; } = string.Empty;
    public string ProviderName { get; set; } = "Mock";
    public VerificationResult Result { get; set; }
    public string? ResponsePayload { get; set; }
    public string? Comments { get; set; }
}
