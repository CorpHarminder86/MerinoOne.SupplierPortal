using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;

namespace MerinoOne.SupplierPortal.Domain.Entities.Supplier;

/// <summary>
/// Supplier bank account (1:N — multiple accounts, one primary). Modeled as <see cref="BaseAggregateRoot"/>
/// (own seccode + tenant + company), NOT a plain <see cref="AuditableEntity"/>: bank CRUD and the
/// seccode RLS filter apply only to <see cref="ISeccode"/> types, so an AuditableEntity here would leak every
/// tenant's bank / IFSC / account numbers on a direct DbSet query (verified AppDbContext.ApplyGlobalFilters).
/// Stamp <c>Owner</c> to the supplier's G-seccode on create.
/// </summary>
public class SupplierBankDetail : BaseAggregateRoot
{
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public string BankName { get; set; } = string.Empty;
    public string BankAddress { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;

    public Guid CurrencyId { get; set; }
    public Currency? Currency { get; set; }

    public string IfscCode { get; set; } = string.Empty;
    public string? SwiftCode { get; set; }
    public bool IsPrimary { get; set; }
    public string? ErpCode { get; set; }
}
