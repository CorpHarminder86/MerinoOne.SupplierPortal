using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// Grants a user access to one company (<see cref="TenantEntity"/>). A user may have many.
/// The mapped company's tenant MUST equal the user's tenant (enforced in the handler validator).
/// One map per user may be flagged <see cref="IsDefault"/> — the fallback active company.
/// </summary>
public class UserCompanyMap : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    public Guid AppUserId { get; set; }
    public AppUser? AppUser { get; set; }

    public Guid TenantEntityId { get; set; }
    public TenantEntity? TenantEntity { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>
    /// Direct full-company grant flag, per (user, company):
    ///   <c>true</c>  = direct full-company grant — the user sees EVERY supplier in this company (incl.
    ///                  future ones). Drives a seccode bypass scoped to this company only.
    ///   <c>false</c> = supplier-derived (today's behaviour) — the company appears in the switcher but the
    ///                  data is seccode-scoped to the user's mapped suppliers.
    /// A user can be company-wise on one company and supplier-wise on another. A direct grant and a
    /// supplier map may coexist on the same company; <c>AllSuppliers=true</c> wins and is never downgraded.
    /// </summary>
    public bool AllSuppliers { get; set; }
}
