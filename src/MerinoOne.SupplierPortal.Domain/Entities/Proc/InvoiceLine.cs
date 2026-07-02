using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

public class InvoiceLine : AuditableEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public Guid? ItemId { get; set; }
    public Item? Item { get; set; }
    public decimal BilledQty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
    public string? TaxCode { get; set; }
    public decimal TaxAmount { get; set; }

    // R6 (2026-07-02) — Invoice generation from ASN. Frozen tax snapshot per line: taxRatePct is the rate
    // resolved from proc.Tax at draft/submit time (re-resolved + frozen at submit); taxId links the governed
    // master (FK RESTRICT — may point at an unshared source company's row, resolved via IgnoreQueryFilters);
    // taxDescription is the display snapshot (snapshot-on-write — read never joins).
    public decimal? TaxRatePct { get; set; }
    public Guid? TaxId { get; set; }
    public Tax? Tax { get; set; }
    public string? TaxDescription { get; set; }
}
