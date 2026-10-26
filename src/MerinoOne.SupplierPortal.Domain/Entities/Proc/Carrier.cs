using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

// R17 (2026-10-26) — Carrier master. LN-fed (POST /inbound/carriers) and company-shared exactly like DeliveryTerm; the
// supplier picks one on the ASN and the ASN snapshots Carrier.Code into Asn.CarrierCode. Description is NULLABLE (LN
// can send a carrier code with no name), unlike every other term master.
public class Carrier : AuditableEntity, ICompanyScoped
{
    /// <summary>Tenant owner — covered by the always-on tenant filter.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// The *source* company the (possibly shared) row is stored under. The company filter for this
    /// type is sharing-aware: TenantEntityId == ResolveSource(Carrier, ActiveCompanyId).
    /// </summary>
    public Guid? TenantEntityId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
