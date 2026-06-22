using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

/// <summary>
/// Supplier license / certification (1:N, with attachments via the polymorphic <c>doc.DocumentUpload</c> table:
/// <c>ownerEntityType='SupplierLicense'</c>, <c>ownerEntityId=supplierLicenseId</c>, <c>DocumentType.License</c>).
/// Modeled as <see cref="BaseAggregateRoot"/> (own seccode) for the same reason as <see cref="SupplierBankDetail"/>:
/// the expiry-reminder dashboard queries this root directly, so it MUST carry seccode RLS. Stamp <c>Owner</c> to
/// the supplier's G-seccode on create.
/// </summary>
public class SupplierLicense : BaseAggregateRoot
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public string LicenseNumber { get; set; } = string.Empty;
    public string LicenseType { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? ErpCode { get; set; }
}
