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
    public string? RejectionReason { get; set; }
    // R4 (2026-06-26) — D2: ProposedDeliveryDate REMOVED. The old date-only propose/approve flow is retired; the
    // single PurchaseOrderNegotiation aggregate is the sole negotiate path (per-line qty/price/date). 2b drops the
    // proposedDeliveryDate column.
    public int Version { get; set; } = 1;
    public string? ErpSyncId { get; set; }
    public string? Notes { get; set; }

    // R5 (TSD R5 Addendum §4.3 / §6) — mandatory ship-to routing. NULLABLE in this phase (backfill is a later
    // phase); the FK gives live linkage back to the resolved admin.CompanyAddress. The DISPLAYED ship-to is the
    // point-in-time snapshot below, not this live address — so later edits to the company address do not change
    // what a historical PO renders. Customer NAME is derived live (tenantEntityId → Company.name), never stored.
    public Guid? ShipToAddressId { get; set; }
    public CompanyAddress? ShipToAddress { get; set; }

    // R5 (TSD R5 Addendum §4.3) — point-in-time ship-to snapshot. Mapped as an OWNED VALUE OBJECT onto the eight
    // shipTo* columns (one VO instead of eight loose properties); written once at inbound resolution. The header
    // renders/reports against this. IsRequired(false) — null until a PO is resolved/backfilled.
    public ShipToSnapshot? ShipTo { get; set; }

    // R5 (TSD R5 Addendum §4.8) — last raw ERP status received (e.g. 'Released', 'modified'). Tracking/audit
    // only; NEVER shown on supplier-facing screens. PoStatus remains the authoritative, displayed status
    // (resolved from erpStatus via the PoStatusMapping master — a later phase).
    public string? ErpStatus { get; set; }

    // Raw ERP-owned PO origin (e.g. the LN order origin / originating document indicator). Stored verbatim from
    // the inbound push for tracking/reference; ERP-owned (overwritten on the next PO inbound sync).
    public string? PoOrigin { get; set; }

    // R11 (2026-07-28, D1/D2) — receiving warehouse code, ERP-owned. Free-text code stored verbatim from the
    // inbound push (no Warehouse master, no FK, no description snapshot); overwritten on the next PO inbound
    // sync. REQUIRED on the inbound contract but NULLABLE here: existing POs keep null until LN re-pushes them
    // (the R5 ShipToAddress precedent — mandatory on the wire, nullable in the DB, no backfill). Flows onto the
    // ASN as the §D4 single-warehouse grouping key and out to LN as the ASN payload's Warehouse.
    public string? Warehouse { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
}
