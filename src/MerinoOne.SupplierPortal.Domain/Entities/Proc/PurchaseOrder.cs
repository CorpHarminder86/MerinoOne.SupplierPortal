using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class PurchaseOrder : BaseAggregateRoot
{
    public string PoNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Guid? BuyerUserId { get; set; }
    public PoType PoType { get; set; }
    public DateTime PoDate { get; set; }
    public string? PaymentTerms { get; set; }
    public string? DeliveryTerms { get; set; }
    public Guid? DeliveryTermId { get; set; }
    public DeliveryTerm? DeliveryTerm { get; set; }
    public Guid? PaymentTermId { get; set; }
    public PaymentTerm? PaymentTerm { get; set; }

    // R4 (2026-06-22) — Addendum A1: PO header currency. FK → mdm.Currency RESTRICT + denormalized code
    // snapshot (Currency is ITenantOwned; FK always valid but carry the code for display without a join).
    // ERP-owned (populated on next PO inbound sync); read-only in the portal.
    public Guid? CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    public string? CurrencyCode { get; set; }
    public PoStatus PoStatus { get; set; } = PoStatus.Released;
    public DateTime? AcknowledgmentAt { get; set; }
    public DateTime? AcceptedAt { get; set; }

    // R12 (2026-07-29, D20) — when the supplier rejected the PO. AcceptedAt's missing twin: the reject path
    // recorded only a reason, so "when was this rejected" was answerable solely by trawling the audit trail.
    // Added for the R12 gate-activation cutoff (D18), which needs a qualifying-transition timestamp per
    // transaction type — accept has AcceptedAt, negotiation has ReviewedAt, reject had nothing. NULL on every
    // PO rejected before this shipped, which the cutoff correctly reads as "pre-activation".
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }
    // R4 (2026-06-26) — D2: ProposedDeliveryDate REMOVED. The old date-only propose/approve flow is retired; the
    // single PurchaseOrderNegotiation aggregate is the sole negotiate path (per-line qty/price/date). 2b drops the
    // proposedDeliveryDate column.
    public int Version { get; set; } = 1;
    public string? ErpSyncId { get; set; }
    public string? Notes { get; set; }

    // R13 (2026-08-05) — ship-to RETIRED. The PO header no longer carries a ship-to FK / 8-col snapshot / a
    // header warehouse. Warehouse is now a PER-LINE receiving address (PurchaseOrderLine.WarehouseAddressId +
    // snapshot); the customer's own BASE address is shown on the PO screen via a LIVE join on tenantEntityId →
    // the company's single base CompanyAddress (never stored on the PO). Customer NAME is likewise derived live.

    // R5 (TSD R5 Addendum §4.8) — last raw ERP status received (e.g. 'Released', 'modified'). Tracking/audit
    // only; NEVER shown on supplier-facing screens. PoStatus remains the authoritative, displayed status
    // (resolved from erpStatus via the PoStatusMapping master — a later phase).
    public string? ErpStatus { get; set; }

    // Raw ERP-owned PO origin (e.g. the LN order origin / originating document indicator). Stored verbatim from
    // the inbound push for tracking/reference; ERP-owned (overwritten on the next PO inbound sync).
    public string? PoOrigin { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}
