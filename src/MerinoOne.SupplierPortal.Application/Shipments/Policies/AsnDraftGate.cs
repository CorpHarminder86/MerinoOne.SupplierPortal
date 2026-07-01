using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Policies;

/// <summary>
/// R4 §6.2 scope, extended to the R5 draft lifecycle: "saving a Draft" and "submission of a Draft ASN" are both
/// inside the confirmation-gate block scope. A Draft ASN's <b>Save Changes</b> (<c>UpdateAsnCommand</c>) and
/// <b>Send For Approval</b> (<c>SendForApprovalCommand</c>) are HARD-BLOCKED when either:
/// <list type="number">
///   <item>a covered PO is not shippable per <see cref="PoConfirmationPolicy"/> (e.g. reset to Released by an ERP
///         Modify) — the same rule the create/submit paths enforce; or</item>
///   <item>another ASN covering the SAME PO is already pending buyer approval (one pending shipment per PO at a
///         time — avoids a second contested shipment against the same order while one awaits the buyer).</item>
/// </list>
/// No supplier override is offered on these paths — the block is hard (admin exceptions still flow through the
/// create/submit override at §6.5). <see cref="EvaluateAsync"/> is the read-only form the DTO builder uses to
/// disable the UI buttons with the same reason.
/// </summary>
public static class AsnDraftGate
{
    /// <summary>Returns the hard-block reason for editing/sending this ASN, or <c>null</c> if allowed. No mutation.</summary>
    public static async Task<string?> EvaluateAsync(
        IAppDbContext db, Guid supplierId, Guid asnId, IReadOnlyCollection<Guid> coveredPoIds, CancellationToken ct)
    {
        if (coveredPoIds.Count == 0) return null;

        // (1) PO confirmation gate — every covered PO must be shippable for this supplier's mode.
        var pos = await db.PurchaseOrders.Where(p => coveredPoIds.Contains(p.Id))
            .Select(p => new { p.PoStatus, p.PoNumber }).ToListAsync(ct);
        var mode = await db.Suppliers.Where(s => s.Id == supplierId)
            .Select(s => s.PoConfirmationMode).FirstOrDefaultAsync(ct);

        foreach (var po in pos)
            if (!PoConfirmationPolicy.AllowsShipping(po.PoStatus, mode))
                return $"PO {po.PoNumber} requires {PoConfirmationPolicy.RequiredAction(mode)} before shipments can be created.";

        // (2) Same-PO pending-approval block: another ASN covering one of these POs is at the buyer awaiting a
        // decision — don't let a second shipment for the same PO be edited or sent until it is approved/rejected.
        var pendingPo = await (from j in db.AsnPurchaseOrders
                               join a in db.Asns on j.AsnId equals a.Id
                               join po in db.PurchaseOrders on j.PurchaseOrderId equals po.Id
                               where coveredPoIds.Contains(j.PurchaseOrderId)
                                     && a.Id != asnId
                                     && a.AsnStatus == AsnStatus.PendingApproval
                               select po.PoNumber).FirstOrDefaultAsync(ct);
        if (pendingPo is not null)
            return $"Another ASN for PO {pendingPo} is pending buyer approval. Wait for it to be approved or rejected before editing or sending this ASN.";

        return null;
    }

    /// <summary>Hard-block guard for Save Changes / Send For Approval. Throws a ValidationException when blocked.</summary>
    public static async Task EnsureEditableAsync(
        IAppDbContext db, Asn asn, IReadOnlyCollection<Guid> coveredPoIds, CancellationToken ct)
    {
        var reason = await EvaluateAsync(db, asn.SupplierId, asn.Id, coveredPoIds, ct);
        if (reason is not null)
            throw new ValidationException(new Dictionary<string, string[]> { ["shipGate"] = new[] { reason } });
    }
}
