using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.StatusMapping;

/// <summary>
/// R5 (TSD R5 Addendum §11 / Component 7) — tenant-scoped reader for the ERP→portal PO-status mapping master
/// (<c>proc.PoStatusMapping</c>). Resolves a raw inbound <c>erpStatus</c> to the portal <see cref="PoStatus"/>
/// it maps to (CASE-INSENSITIVE per §4.7). Returns null when the status has no active mapping row for the
/// tenant (UNMAPPED — the inbound handler then writes a Sync Log Failed row and leaves PoStatus unchanged,
/// §11.3). Cached like the other settings readers; invalidated when a mapping is saved/deleted.
/// </summary>
public interface IPoStatusMap
{
    /// <summary>
    /// Resolves <paramref name="erpStatus"/> for <paramref name="tenantId"/> to its mapped portal status, or
    /// null when there is no active mapping (UNMAPPED) or the raw status is null/blank. Case-insensitive.
    /// </summary>
    PoStatus? Resolve(Guid? tenantId, string? erpStatus);
}
