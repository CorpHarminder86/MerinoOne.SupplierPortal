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
///   <item>another ASN covering the SAME PO is already in flight — pending buyer approval, or approved and
///         awaiting post (one in-flight shipment per PO at a time — avoids a second contested shipment against
///         the same order while one holds an unconsumed claim on it).</item>
/// </list>
/// No supplier override is offered on these paths — the block is hard (admin exceptions still flow through the
/// create/submit override at §6.5). <see cref="EvaluateAsync"/> is the read-only form the DTO builder uses to
/// disable the UI buttons with the same reason.
///
/// <para><b>R14 — deliberately NOT called on the Post path.</b> Post reaches <c>AsnSubmitExecutor</c>, which
/// re-evaluates PO shippability through <c>PoConfirmationGateEnforcer</c> — and that carries the §6.5 admin
/// override, whereas this gate is a hard block. Running this gate at Post would short-circuit the override and
/// leave an ASN unshippable by anyone when an ERP Modify resets a covered PO after approval.</para>
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

        // (2) Same-PO in-flight block: another ASN covering one of these POs is at the buyer awaiting a decision,
        // or has been confirmed but not yet posted — don't let a second shipment for the same PO be edited or sent
        // until that one posts, is rejected, or is cancelled.
        //
        // R14 — the state set WIDENED to include Approved. Before R14 approval consumed the PO balance immediately,
        // so PendingApproval was the whole window of contention; now balance is consumed at POST, so an Approved
        // ASN is still holding an unconsumed claim on the PO and must keep the lock.
        //
        // R14 fix (F1) — the "other ASN" side used to read the AsnPurchaseOrder junction ONLY, so a
        // schedule-built ASN (whose junction is empty; its PO linkage lives on AsnLine → PurchaseOrderLine)
        // slipped the lock entirely. Resolve the other ASNs' covered POs from ALL THREE sources, the same union
        // AsnApprovalSupport.ResolveCoveredPoIdsAsync uses: line-derived, junction, and the legacy scalar header.
        // NOTE: compare the status with explicit ||, NOT a local array + Contains. An `AsnStatus[]` closure hits
        // the ReadOnlySpan Contains overload during expression evaluation and throws a TypeLoadException before
        // EF ever gets to translate it.
        var inFlight = db.Asns.Where(a =>
            a.Id != asnId && !a.IsDeleted &&
            (a.AsnStatus == AsnStatus.PendingApproval || a.AsnStatus == AsnStatus.Approved));

        // Three separate look-ups rather than one Concat — same union, and each translates on its own.
        var blockedPoId = await (from a in inFlight
                                 join al in db.AsnLines on a.Id equals al.AsnId
                                 join pol in db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                                 where !al.IsDeleted && coveredPoIds.Contains(pol.PurchaseOrderId)
                                 select (Guid?)pol.PurchaseOrderId).FirstOrDefaultAsync(ct);

        blockedPoId ??= await (from a in inFlight
                               join j in db.AsnPurchaseOrders on a.Id equals j.AsnId
                               where !j.IsDeleted && coveredPoIds.Contains(j.PurchaseOrderId)
                               select (Guid?)j.PurchaseOrderId).FirstOrDefaultAsync(ct);

        blockedPoId ??= await inFlight
            .Where(a => a.PurchaseOrderId != null && coveredPoIds.Contains(a.PurchaseOrderId.Value))
            .Select(a => a.PurchaseOrderId)
            .FirstOrDefaultAsync(ct);

        if (blockedPoId is { } blocked)
        {
            var poNumber = await db.PurchaseOrders.Where(p => p.Id == blocked)
                .Select(p => p.PoNumber).FirstOrDefaultAsync(ct);
            return $"Another ASN for PO {poNumber} is pending buyer approval or awaiting post. Wait for it to be posted, rejected or cancelled before editing or sending this ASN.";
        }

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
