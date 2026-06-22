using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R4 (2026-06-22) — Module 3 (Q1): junction that lets one ASN span multiple POs. It is a CHILD of the ASN
/// aggregate (NOT an aggregate root): it carries no seccode/tenant of its own — the owning <see cref="Asn"/>
/// root carries the seccode RLS, and these rows are reached only through the ASN. Modeled as
/// <see cref="AuditableEntity"/> (two-key + audit block only), mirroring <c>SupplierChangeRequestLine</c>.
/// </summary>
public class AsnPurchaseOrder : AuditableEntity
{
    public Guid AsnId { get; set; }
    public Asn? Asn { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
}
