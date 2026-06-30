using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// R5 (TSD R5 Addendum §4.1 / §5) — the CUSTOMER (buying entity) the supplier ships to. Keyed 1:1 to a
/// <c>tenantEntityId</c> (the buying entity that maps PurchaseOrder.tenantEntityId). Holds the customer
/// display name shown on the PO header (derived live, never stored on the PO). Owns one or more
/// <see cref="CompanyAddress"/> rows (the named, ERP-mappable ship-to addresses).
/// Aggregate root: audit + seccode + RowVersion + tenant scope come from <see cref="BaseAggregateRoot"/>.
/// </summary>
public class Company : BaseAggregateRoot
{
    // NOTE: the buying entity is the inherited BaseAggregateRoot.TenantEntityId (ITenantScoped) — it IS both the
    // tenant-scope column AND the §4.1 business column "the buying entity; maps PurchaseOrder.tenantEntityId".
    // It is NOT redeclared here; the base convention maps it to column `tenantEntityId`. The §4.1
    // UQ_Company_tenant_entity index is on (TenantId, TenantEntityId), both from the base.

    /// <summary>Customer / company display name (shown on the PO header).</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public ICollection<CompanyAddress> Addresses { get; set; } = new List<CompanyAddress>();
}
