using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// R5 (TSD R5 Addendum §4.2 / §5) — a named, ERP-mappable ship-to address hung directly off a
/// <see cref="TenantEntity"/> (the customer / buying company — [[r5-consolidation]], was the dropped
/// admin.Company). Mirrors <c>supplier.SupplierAddress</c> (and reuses the same Blazor address control), plus:
/// <list type="bullet">
///   <item><b>AddressName</b> — MANDATORY human label for the ship-to (e.g. "Bhiwandi DC").</item>
///   <item><b>ErpCode</b> — OPTIONAL; the key the inbound PO ship-to resolves against. Unique within the
///   tenant entity when present (filtered-unique index), so inbound resolution is deterministic (§6.2).</item>
/// </list>
/// Audit + soft-delete come from <see cref="AuditableEntity"/> (mirrors SupplierAddress); tenant/seccode scope
/// is carried by the owning <see cref="TenantEntity"/>.
/// </summary>
public class CompanyAddress : AuditableEntity
{
    public Guid TenantEntityId { get; set; }
    public TenantEntity? TenantEntity { get; set; }

    /// <summary>NEW (§4.2) — mandatory label for the ship-to.</summary>
    public string AddressName { get; set; } = string.Empty;

    /// <summary>NEW (§4.2) — optional; matched by the inbound PO shipToAddress code. Unique per tenant entity when set.</summary>
    public string? ErpCode { get; set; }

    /// <summary>Registered | Billing | Shipping | Plant | Other.</summary>
    public string AddressType { get; set; } = string.Empty;

    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Pincode { get; set; }
    public string Country { get; set; } = "India";

    public bool IsActive { get; set; } = true;
}
