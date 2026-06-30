using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// R5 (TSD R5 Addendum §4.2 / §5) — a named, ERP-mappable address under a <see cref="Company"/>. Mirrors
/// <c>supplier.SupplierAddress</c> (and reuses the same Blazor address control), plus two new fields:
/// <list type="bullet">
///   <item><b>AddressName</b> — MANDATORY human label for the ship-to (e.g. "Bhiwandi DC").</item>
///   <item><b>ErpCode</b> — OPTIONAL; the key the inbound PO ship-to resolves against. Unique within the
///   company when present (filtered-unique index), so inbound resolution is deterministic (§6.2).</item>
/// </list>
/// Audit + soft-delete come from <see cref="AuditableEntity"/> (mirrors SupplierAddress); tenant/seccode scope
/// is carried by the owning <see cref="Company"/> aggregate.
/// </summary>
public class CompanyAddress : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }

    /// <summary>NEW (§4.2) — mandatory label for the ship-to.</summary>
    public string AddressName { get; set; } = string.Empty;

    /// <summary>NEW (§4.2) — optional; matched by the inbound PO shipToAddress code. Unique per company when set.</summary>
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
