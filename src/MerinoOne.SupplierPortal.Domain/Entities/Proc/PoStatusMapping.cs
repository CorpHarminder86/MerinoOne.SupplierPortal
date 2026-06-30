using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R5 (TSD R5 Addendum §4.7 / Component 7) — ERP-to-portal PO status mapping master.
/// Tenant-scoped (tenantId from the inherited ITenantScoped via BaseAggregateRoot); many ERP
/// statuses may map to one portal status. The unique index <c>UQ_PoStatusMapping_tenant_erp</c>
/// (filtered on isDeleted = 0) ensures one portal status per ERP status per tenant.
///
/// Case-insensitivity: the application resolves erpStatus CASE-INSENSITIVELY. The DB collation
/// on merino-supplier-dev is SQL_Latin1_General_CP1_CI_AS (verified 2026-06-30), which is
/// Case-Insensitive (CI), so the filtered unique index UQ_PoStatusMapping_tenant_erp on
/// (tenantId, erpStatus) is naturally CI without requiring an explicit COLLATE clause on the
/// column or index.
/// </summary>
public class PoStatusMapping : BaseAggregateRoot
{
    /// <summary>Raw ERP status value as received inbound (e.g. 'Released', 'modified', 'Created').</summary>
    public string ErpStatus { get; set; } = string.Empty;

    /// <summary>Target portal PoStatus enum name this ERP status maps to (e.g. 'Released', 'Draft').</summary>
    public string PoStatus { get; set; } = string.Empty;

    /// <summary>Whether this mapping row is active. Inactive rows are excluded from resolution.</summary>
    public bool IsActive { get; set; } = true;
}
