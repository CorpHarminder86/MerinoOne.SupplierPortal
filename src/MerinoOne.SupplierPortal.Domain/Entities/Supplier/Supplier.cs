using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
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

    // R4 (2026-06-22) — Module 1: term/currency FKs + denormalized snapshots, PO-response mode, ERP code.
    // FKs reference the physical …Id (always referentially valid). PaymentTerm/DeliveryTerm are ICompanyScoped
    // (sharing-aware) so a row may belong to an unshared source company — carry the code snapshot for display
    // (same dual-pattern PurchaseOrder uses). Currency is ITenantOwned. All FKs RESTRICT.
    public Guid? CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    public Guid? PaymentTermId { get; set; }
    public PaymentTerm? PaymentTerm { get; set; }
    public Guid? DeliveryTermId { get; set; }
    public DeliveryTerm? DeliveryTerm { get; set; }
    public string? PaymentTermCode { get; set; }
    public string? DeliveryTermCode { get; set; }

    // R4 (2026-06-26) — TSD R4 Addendum §3.4 / D1: the PO confirmation mode that drives the ship-gate (§6.2).
    // REPLACES the old PoResponseMode {Manual,Auto}; the DB COLUMN stays "poResponseMode" (HasColumnName), so 2b
    // only data-migrates the stored values (Manual→AcceptToShip, Auto→AutoAccept) — no column rename. Default
    // AcceptToShip (the strictest, gate-never-undefined default).
    public PoConfirmationMode PoConfirmationMode { get; set; } = PoConfirmationMode.AcceptToShip;
    public string? ErpCode { get; set; }

    // R4 (2026-06-26) — TSD R4 Addendum §3.4, Component 3 (PO Confirmation Gate). Per-supplier action toggles
    // that accompany the confirmation mode: whether the supplier may negotiate (propose new terms) or reject/
    // decline a PO. NOT NULL DEFAULT 1 so existing rows are safe. AllowReject gates /reject; AllowNegotiate gates
    // the PO Negotiation create (CreatePoNegotiationCommand).
    public bool AllowNegotiate { get; set; } = true;
    public bool AllowReject { get; set; } = true;

    public ICollection<SupplierVerification> Verifications { get; set; } = new List<SupplierVerification>();
    public ICollection<SupplierAddress> Addresses { get; set; } = new List<SupplierAddress>();
    public ICollection<SupplierContact> Contacts { get; set; } = new List<SupplierContact>();
    public ICollection<SupplierBankDetail> BankDetails { get; set; } = new List<SupplierBankDetail>();
    public ICollection<SupplierLicense> Licenses { get; set; } = new List<SupplierLicense>();
}
