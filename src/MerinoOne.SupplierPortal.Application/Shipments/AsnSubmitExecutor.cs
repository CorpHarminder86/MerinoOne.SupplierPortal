using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §10.2 / §10.4) — the SINGLE point of truth for the ASN final-submit transition. Extracted
/// from the R4 <c>SubmitAsnCommand</c> so the <b>Approve → Submit</b> path (the only way an ASN reaches Submitted in
/// R5) reuses the EXACT same logic. In ONE transaction it:
/// <list type="number">
///   <item>validates serial/lot (per Item flags), intra/cross-ASN uniqueness, the nominal over-ship message, the
///         PO confirmation gate, and the single-currency guard;</item>
///   <item><b>runs the authoritative atomic over-ship guard</b> — the R4 conditional <c>UPDATE</c>
///         (<c>orderQty×factor − shippedQtyToDate ≥ shipQty</c>, behind <c>Fulfilment.EnforceOverShipGuard</c>, WITH
///         the rounded-cap pre-check) — that <b>consumes <c>shippedQtyToDate</c></b>. This is the move from R4 §4.3:
///         the guard NO LONGER fires at ASN create; it fires ONCE, here, at final Submit (§10.4). If the guard
///         returns 0 rows (balance lost post-approval, UC-AP-05) the submit fails with the R4 over-ship message;</item>
///   <item>flips the ASN to <c>Submitted</c>, stamps <c>submittedAt/by</c> + the ERP correlation key;</item>
///   <item>creates EXACTLY ONE draft invoice (<see cref="DraftInvoiceFromAsnFactory"/>, upsert-or-skip) and enqueues
///         the ASN→ERP outbox post (post-commit dispatch).</item>
/// </list>
///
/// <para><b>What it does NOT do (moved out in R5):</b> the attachment-requirement check — that now fires at
/// <c>SendForApproval</c> (§10.3), so the executor assumes attachments were already cleared at send-for-approval.</para>
///
/// <para>The caller (Approve handler, or the back-compat SubmitAsnCommand) is responsible for the from-state
/// assertion and the surrounding <c>SaveChangesAsync</c>: this executor stages the consumption UPDATE inside its OWN
/// explicit transaction, then the ASN mutation + invoice + outbox commit on the shared context's SaveChanges + the
/// executor's own transaction commit.</para>
/// </summary>
public sealed class AsnSubmitExecutor
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IOutboxDispatcher _outbox;
    private readonly DraftInvoiceFromAsnFactory _invoiceFactory;
    private readonly IFulfilmentSettings _fulfilment;

    public AsnSubmitExecutor(
        IAppDbContext db, ICurrentUser user, IOutboxDispatcher outbox, DraftInvoiceFromAsnFactory invoiceFactory,
        IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _outbox = outbox; _invoiceFactory = invoiceFactory; _fulfilment = fulfilment;
    }

    /// <summary>
    /// Runs the full submit transition for an ASN already validated to be in the correct from-state. Consumes
    /// balance via the atomic guard (§10.4), flips to Submitted, creates the draft invoice + enqueues the ERP post.
    /// Throws <see cref="ValidationException"/> (→ 400) on a serial/lot/over-ship failure (including the
    /// balance-lost-at-submit case, UC-AP-05).
    /// </summary>
    public async Task ExecuteAsync(Asn asn, DateTime now, string? overrideReason, CancellationToken ct)
    {
        // ---- Line context + serial/lot + over-ship validation -------------------------------------------
        var lineCtx = await (from al in _db.AsnLines
                             join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                             where al.AsnId == asn.Id
                             select new
                             {
                                 al.Id,
                                 al.PurchaseOrderLineId,
                                 al.ShippedQty,
                                 al.BatchNumber,
                                 al.PositionNo,
                                 pol.OrderQty,
                                 pol.ItemCode,
                                 pol.ItemId,
                                 pol.ShippedQtyToDate,
                             }).ToListAsync(ct);

        if (lineCtx.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "Cannot submit an ASN with no lines." }
            });

        // Item control flags (Addendum A3) for lot/serial validation + tolerance. Resolve by ItemCode within the
        // ASN's company (the PO line's ItemId is routinely null). IgnoreQueryFilters — Item is company-scoped.
        var asnCompany = asn.TenantEntityId;
        var itemCodes = lineCtx.Select(l => l.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemRows = await _db.Items.IgnoreQueryFilters()
            .Where(i => i.TenantEntityId == asnCompany && !i.IsDeleted && itemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsLotControlled, i.IsSerialized, i.OverShipTolerancePct })
            .ToListAsync(ct);
        var itemFlags = itemRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

        // SupplierItem tolerance overrides (§7.1) for the per-line guard factor.
        var resolvedItemIds = lineCtx
            .Select(l => l.ItemId ?? (!string.IsNullOrWhiteSpace(l.ItemCode) && itemFlags.TryGetValue(l.ItemCode, out var fr) ? fr.Id : (Guid?)null))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var supplierItemTol = (await _db.SupplierItems.IgnoreQueryFilters()
                .Where(si => !si.IsDeleted && si.SupplierId == asn.SupplierId && resolvedItemIds.Contains(si.ItemId))
                .Select(si => new { si.ItemId, si.OverShipTolerancePct })
                .ToListAsync(ct))
            .ToDictionary(si => si.ItemId, si => si.OverShipTolerancePct);

        decimal ResolveLineTolerancePct(string? itemCode, Guid? itemIdRaw)
        {
            var flags = !string.IsNullOrWhiteSpace(itemCode) && itemFlags.TryGetValue(itemCode!, out var f) ? f : null;
            var itemId = itemIdRaw ?? flags?.Id;
            decimal? siTol = itemId is { } id && supplierItemTol.TryGetValue(id, out var st) ? st : null;
            var itemTol = flags?.OverShipTolerancePct ?? 0m;
            return siTol ?? itemTol;
        }

        // Serial / lot children for this ASN's lines.
        var thisLineIds = lineCtx.Select(l => l.Id).ToList();
        var serialsByLine = (await _db.AsnLineSerials
                .Where(s => thisLineIds.Contains(s.AsnLineId))
                .Select(s => new { s.AsnLineId, s.SerialNumber })
                .ToListAsync(ct))
            .GroupBy(s => s.AsnLineId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.SerialNumber).ToList());
        var lotsByLine = (await _db.AsnLineLots
                .Where(l => thisLineIds.Contains(l.AsnLineId))
                .Select(l => new { l.AsnLineId, l.LotNo, l.Qty })
                .ToListAsync(ct))
            .GroupBy(l => l.AsnLineId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.LotNo, x.Qty)).ToList());

        // Covered PO id(s) for the cross-ASN uniqueness scope + the confirmation gate: junction + legacy scalar.
        var coveredPoIds = await _db.AsnPurchaseOrders
            .Where(j => j.AsnId == asn.Id && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);
        var coveredPoSet = coveredPoIds.ToHashSet();
        if (asn.PurchaseOrderId.HasValue) coveredPoSet.Add(asn.PurchaseOrderId.Value);
        // R5 — for schedule-built ASNs the header PO is null; resolve covered POs from the lines' PO lines.
        var linePoIds = await _db.AsnLines
            .Where(al => al.AsnId == asn.Id && !al.IsDeleted)
            .Join(_db.PurchaseOrderLines, al => al.PurchaseOrderLineId, pol => pol.Id, (al, pol) => pol.PurchaseOrderId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var poId in linePoIds) coveredPoSet.Add(poId);
        var poIdList = coveredPoSet.ToList();

        // PO confirmation gate re-eval per covered PO (a mid-fulfilment ERP Modify can have reset a PO to Released).
        var coveredPos = await _db.PurchaseOrders.Where(p => poIdList.Contains(p.Id)).ToListAsync(ct);
        var confirmationMode = await _db.Suppliers.Where(s => s.Id == asn.SupplierId)
            .Select(s => s.PoConfirmationMode).FirstOrDefaultAsync(ct);
        Policies.PoConfirmationGateEnforcer.Enforce(
            _db, coveredPos, confirmationMode, overrideReason, _user, asn.Id, asn.AsnNumber, now);

        var errors = new Dictionary<string, List<string>>();
        void AddErr(string key, string msg)
        {
            if (!errors.TryGetValue(key, out var list)) { list = new(); errors[key] = list; }
            list.Add(msg);
        }

        var allSerials = new List<string>();
        var allLotNos = new List<string>();

        foreach (var l in lineCtx)
        {
            if (string.IsNullOrWhiteSpace(l.ItemCode) || !itemFlags.TryGetValue(l.ItemCode, out var flags)) continue;

            if (flags.IsSerialized)
            {
                var serials = serialsByLine.TryGetValue(l.Id, out var sl) ? sl : new List<string>();
                if (l.ShippedQty != Math.Truncate(l.ShippedQty))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is serialized; ShippedQty must be a whole number.");
                else if (serials.Count != (int)l.ShippedQty)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is serialized; expected {(int)l.ShippedQty} serial(s) but got {serials.Count}.");
                if (serials.Any(string.IsNullOrWhiteSpace))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has an empty serial number.");
                allSerials.AddRange(serials.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            }
            else if (flags.IsLotControlled)
            {
                var lots = lotsByLine.TryGetValue(l.Id, out var ll) ? ll : new List<(string LotNo, decimal Qty)>();
                if (lots.Count == 0)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is lot-controlled; at least one lot is required.");
                if (lots.Any(x => string.IsNullOrWhiteSpace(x.LotNo)))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has a lot with an empty lot number.");
                if (lots.Any(x => x.Qty <= 0))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has a lot with a non-positive quantity.");
                var lotSum = lots.Sum(x => x.Qty);
                if (lotSum != l.ShippedQty)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is lot-controlled; Σ(lot qty) {lotSum} must equal ShippedQty {l.ShippedQty}.");
                allLotNos.AddRange(lots.Where(x => !string.IsNullOrWhiteSpace(x.LotNo)).Select(x => x.LotNo.Trim()));
            }
        }

        foreach (var dup in allSerials.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
            AddErr("lines", $"Serial number '{dup}' appears more than once on this ASN.");
        foreach (var dup in allLotNos.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
            AddErr("lines", $"Lot number '{dup}' appears more than once on this ASN.");

        if (poIdList.Count > 0)
        {
            var otherAsnIdsViaJunction = _db.AsnPurchaseOrders
                .Where(j => !j.IsDeleted && j.AsnId != asn.Id && poIdList.Contains(j.PurchaseOrderId))
                .Select(j => j.AsnId);
            var otherAsnIdsViaScalar = _db.Asns
                .Where(a => a.Id != asn.Id && !a.IsDeleted && a.PurchaseOrderId != null && poIdList.Contains(a.PurchaseOrderId.Value))
                .Select(a => a.Id);
            // R5 — schedule-built ASNs have no junction/scalar PO; also cover ASNs whose LINES reference these POs.
            var otherAsnIdsViaLine = _db.AsnLines
                .Where(al => !al.IsDeleted && al.AsnId != asn.Id)
                .Join(_db.PurchaseOrderLines, al => al.PurchaseOrderLineId, pol => pol.Id, (al, pol) => new { al.AsnId, pol.PurchaseOrderId })
                .Where(x => poIdList.Contains(x.PurchaseOrderId))
                .Select(x => x.AsnId);
            var otherLineIds = _db.AsnLines
                .Where(al => !al.IsDeleted
                             && (otherAsnIdsViaJunction.Contains(al.AsnId)
                                 || otherAsnIdsViaScalar.Contains(al.AsnId)
                                 || otherAsnIdsViaLine.Contains(al.AsnId)))
                .Select(al => al.Id);

            if (allSerials.Count > 0)
            {
                var clash = await _db.AsnLineSerials
                    .Where(s => otherLineIds.Contains(s.AsnLineId) && allSerials.Contains(s.SerialNumber))
                    .Select(s => s.SerialNumber).Distinct().ToListAsync(ct);
                foreach (var s in clash)
                    AddErr("lines", $"Serial number '{s}' is already used on another ASN for the same PO.");
            }
            if (allLotNos.Count > 0)
            {
                var clash = await _db.AsnLineLots
                    .Where(l => otherLineIds.Contains(l.AsnLineId) && allLotNos.Contains(l.LotNo))
                    .Select(l => l.LotNo).Distinct().ToListAsync(ct);
                foreach (var l in clash)
                    AddErr("lines", $"Lot number '{l}' is already used on another ASN for the same PO.");
            }
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));

        // ---- AUTHORITATIVE atomic over-ship guard — consumes balance (§10.4, MOVED from ASN create) ------
        // Aggregate the requested ship qty per PO line so the cumulative is maintained once per line.
        var shipQtyByPoLine = lineCtx
            .GroupBy(l => l.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => (Qty: g.Sum(x => x.ShippedQty),
                                            x: g.First()));
        var enforceGuard = _fulfilment.EnforceOverShipGuard;
        var rounding = _fulfilment.OverShipAllowanceRounding;

        await using var tx = await _db.BeginTransactionAsync(ct);

        foreach (var (poLineId, info) in shipQtyByPoLine)
        {
            var shipQty = info.Qty;
            if (enforceGuard)
            {
                var factor = OverShipTolerance.Factor(ResolveLineTolerancePct(info.x.ItemCode, info.x.ItemId));
                if (rounding != OverShipRoundingMode.None)
                {
                    var roundedCap = OverShipTolerance.RoundAllowance(
                        Math.Max(0m, (info.x.OrderQty * factor) - info.x.ShippedQtyToDate), rounding);
                    if (shipQty > roundedCap)
                        throw new ValidationException(new Dictionary<string, string[]>
                        {
                            ["shippedQty"] = new[] { "Ship qty exceeds order qty plus over-ship tolerance." }
                        });
                }

                var affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId && (l.OrderQty * factor) - l.ShippedQtyToDate >= shipQty)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + shipQty), ct);

                // 0 rows = the conditional ceiling failed (incl. UC-AP-05: balance lost post-approval).
                if (affected == 0)
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["shippedQty"] = new[] { "Ship qty exceeds order qty plus over-ship tolerance." }
                    });
            }
            else
            {
                var affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + shipQty), ct);
                if (affected == 0)
                    throw new Common.Exceptions.NotFoundException("PurchaseOrderLine", poLineId);
            }
        }

        // ---- Flip to Submitted + stamp + ERP correlation key --------------------------------------------
        var key = OutboxKey.For(OutboxEntity.Asn, asn.TenantId, asn.AsnNumber, "submit");
        asn.AsnStatus = AsnStatus.Submitted;
        asn.SubmittedAt = now;
        asn.SubmittedBy = _user.UserCode;
        asn.ErpSyncId = key;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // ---- Single draft invoice spanning all the ASN's POs (R16 currency guard inside; upsert-or-skip) -
        await _invoiceFactory.EnsureDraftAsync(asn, now, ct);

        // ---- Outbox: ASN -> ERP post, dispatched POST-COMMIT --------------------------------------------
        await _outbox.EnqueueAsync(OutboxTransactionType.AsnPost, OutboxEntity.Asn, asn.Id, key, null, ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
