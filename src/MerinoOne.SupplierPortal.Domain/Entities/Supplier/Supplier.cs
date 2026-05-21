using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

public class Supplier : BaseAggregateRoot
{
    public string SupplierCode { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? TradeName { get; set; }
    public SupplierType SupplierType { get; set; }
    public string? GstNumber { get; set; }
    public string? PanNumber { get; set; }
    public string? MsmeRegNumber { get; set; }
    public string? MsmeCategory { get; set; }
    public bool GstValidated { get; set; }
    public bool PanValidated { get; set; }
    public bool MsmeValidated { get; set; }
    public RegistrationStatus RegistrationStatus { get; set; } = RegistrationStatus.Invited;
    public string? InvitedBy { get; set; }
    public DateTime? InvitedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovalOverrideComment { get; set; }
    public string? RejectionReason { get; set; }
    public string? Website { get; set; }
    public bool IsActiveSupplier { get; set; }

    public ICollection<SupplierVerification> Verifications { get; set; } = new List<SupplierVerification>();
    public ICollection<SupplierAddress> Addresses { get; set; } = new List<SupplierAddress>();
    public ICollection<SupplierContact> Contacts { get; set; } = new List<SupplierContact>();
}
