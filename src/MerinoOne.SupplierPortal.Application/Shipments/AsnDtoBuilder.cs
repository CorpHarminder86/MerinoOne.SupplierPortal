using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R4 (2026-06-22) — Module 3. Builds the multi-PO-aware <see cref="AsnDetailDto"/> for the ASN commands/queries
/// (Create / Update / Submit / GetById). Centralises the covered-PO list (junction OR legacy scalar PO),
/// per-line PO context + position/sequence snapshot, the locked-on-submit flag, the linked draft-invoice id,
/// and the ASN attachment projection — so every handler returns an identical shape.
/// </summary>
public static class AsnDtoBuilder
{
    /// <summary>
    /// R5 — True once the ASN is locked against supplier edits / attachment-mutation. Editable states are Draft,
    /// Rejected (a rejected ASN returns to the supplier for edit, §10.1) and — R14 (D5) — Approved, so the
    /// supplier can complete shipment references and upload the Packing List / Invoice after the buyer confirms.
    /// PendingApproval (the buyer is reviewing a stable set) / Submitted / InTransit / Delivered / Cancelled are
    /// locked. This is also the single source of truth for the attachment upload/delete lock in
    /// <c>DocumentUploadsController</c>.
    /// </summary>
    public static bool IsLocked(AsnStatus status)
        => status is not (AsnStatus.Draft or AsnStatus.Rejected or AsnStatus.Approved);

    public static async Task<AsnDetailDto> BuildAsync(IAppDbContext db, Guid asnId, CancellationToken ct,
        Policies.OverShipRoundingMode rounding = Policies.OverShipRoundingMode.None)
    {
        var a = await db.Asns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == asnId, ct)
                ?? throw new Common.Exceptions.NotFoundException("Asn", asnId);

        // R14 — the supplier's LegalName AND its AsnConfirmationRequired flag come from the same row; the flag
        // decides which lifecycle the UI must render (Send-for-approval vs Post straight from Draft).
        var supplier = await db.Suppliers.Where(s => s.Id == a.SupplierId)
            .Select(s => new { s.LegalName, s.AsnConfirmationRequired })
            .FirstOrDefaultAsync(ct);
        var supplierName = supplier?.LegalName ?? string.Empty;
        var asnConfirmationRequired = supplier?.AsnConfirmationRequired ?? true;

        // Covered POs: union of the junction rows and the legacy scalar header PO (single-PO back-compat).
        var junctionPoIds = await db.AsnPurchaseOrders
            .Where(j => j.AsnId == asnId && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);

        var coveredPoIds = junctionPoIds.ToHashSet();
        if (a.PurchaseOrderId.HasValue) coveredPoIds.Add(a.PurchaseOrderId.Value);

        var poById = await db.PurchaseOrders
            .Where(p => coveredPoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PoNumber, p.CurrencyCode })
            .ToDictionaryAsync(p => p.Id, ct);

        var coveredPos = coveredPoIds
            .Select(id => poById.TryGetValue(id, out var p)
                ? new AsnPurchaseOrderDto(p.Id, p.PoNumber, p.CurrencyCode)
                : new AsnPurchaseOrderDto(id, "(unknown)", null))
            .OrderBy(p => p.PoNumber)
            .ToList();

        string? headerPoNumber = a.PurchaseOrderId.HasValue && poById.TryGetValue(a.PurchaseOrderId.Value, out var hp)
            ? hp.PoNumber : null;

        // Lines join their PO line for item/qty + owning PO number. Projected to an intermediate row first so the
        // serial/lot children (loaded separately below) can be merged in by line id without an N+1 per line.
        // R4 (2026-06-26) — also carry the PO line's cumulative ShippedQtyToDate + the PO's supplier/company so the
        // derived Balance + OverShipAllowance (§7.3 / DI-04) can be computed per line below.
        // R15 — also left-join the line's originating delivery schedule (nullable back-link) so the wizard can
        // pre-check the schedule rows in its step-1 grid and show the schedule's delivery date on Quantities.
        var lineRows = await (from al in db.AsnLines
                              join pol in db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                              join po in db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                              join schJ in db.DeliverySchedules on al.DeliveryScheduleId equals (Guid?)schJ.Id into schJoin
                              from sch in schJoin.DefaultIfEmpty()
                              where al.AsnId == asnId
                              orderby po.PoNumber, pol.PositionNo
                              select new
                              {
                                  al.Id,
                                  al.PurchaseOrderLineId,
                                  pol.PurchaseOrderId,
                                  po.PoNumber,
                                  PoSupplierId = po.SupplierId,
                                  PoCompany = po.TenantEntityId,
                                  PoPositionNo = pol.PositionNo,
                                  al.PositionNo,
                                  al.SequenceNo,
                                  pol.ItemCode,
                                  pol.ItemId,
                                  pol.ItemDescription,
                                  pol.OrderUnit,
                                  pol.OrderQty,
                                  pol.ShippedQtyToDate,
                                  al.ShippedQty,
                                  al.BatchNumber,
                                  al.ExpiryDate,
                                  al.DeliveryScheduleId,
                                  ScheduleDeliveryDate = sch != null ? (DateTime?)sch.DeliveryDate : null,
                              }).ToListAsync(ct);

        // R4 (2026-06-26) — resolve the per-line over-ship tolerance (§7.1 SupplierItem ?? Item) so the derived
        // OverShipAllowance can be surfaced. Items are company-scoped with natural key (TenantEntityId, Code) and
        // the PO line's ItemId is often null — resolve the Item by ItemId OR by (company, ItemCode), then the
        // SupplierItem override by (supplierId, resolvedItemId). IgnoreQueryFilters mirrors the create path
        // (Item may live in an unshared source company; SupplierItem is tenant/seccode-owned but the read is a
        // build-time projection, not a mutation).
        var tolItemCodes = lineRows.Select(r => r.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var tolCompanies = lineRows.Select(r => r.PoCompany).Distinct().ToList();
        var itemTolRows = await db.Items.IgnoreQueryFilters()
            .Where(i => !i.IsDeleted && tolCompanies.Contains(i.TenantEntityId) && tolItemCodes.Contains(i.Code))
            .Select(i => new { i.Id, i.TenantEntityId, i.Code, i.OverShipTolerancePct })
            .ToListAsync(ct);
        // (company, codeLower) -> (itemId, itemTol)
        var itemTolByCompanyCode = itemTolRows
            .GroupBy(i => (i.TenantEntityId, Code: i.Code.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().OverShipTolerancePct));
        var itemTolById = itemTolRows
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.First().OverShipTolerancePct);

        // Resolve each line's Item id (explicit or by company+code) up front so SupplierItem overrides can be batched.
        Guid? ResolveItemId(Guid? itemId, Guid? company, string? code)
        {
            if (itemId is { } id) return id;
            if (!string.IsNullOrWhiteSpace(code)
                && itemTolByCompanyCode.TryGetValue((company, code!.ToLowerInvariant()), out var hit))
                return hit.Id;
            return null;
        }

        var supplierItemKeys = lineRows
            .Select(r => (r.PoSupplierId, ItemId: ResolveItemId(r.ItemId, r.PoCompany, r.ItemCode)))
            .Where(k => k.ItemId is not null)
            .Distinct()
            .ToList();
        var supplierIdsForTol = supplierItemKeys.Select(k => k.PoSupplierId).Distinct().ToList();
        var itemIdsForTol = supplierItemKeys.Select(k => k.ItemId!.Value).Distinct().ToList();
        var supplierItemTol = (await db.SupplierItems.IgnoreQueryFilters()
                .Where(si => !si.IsDeleted && supplierIdsForTol.Contains(si.SupplierId) && itemIdsForTol.Contains(si.ItemId))
                .Select(si => new { si.SupplierId, si.ItemId, si.OverShipTolerancePct })
                .ToListAsync(ct))
            .ToDictionary(si => (si.SupplierId, si.ItemId), si => si.OverShipTolerancePct);

        // Per-line resolved tolerance %: SupplierItem override (non-null) ?? Item floor ?? 0 (no resolvable item).
        decimal ResolveTolerancePct(Guid supplierId, Guid? company, Guid? itemIdRaw, string? code)
        {
            var itemId = ResolveItemId(itemIdRaw, company, code);
            if (itemId is null) return 0m;
            decimal? itemTol = itemTolById.TryGetValue(itemId.Value, out var it) ? it : null;
            decimal? siTol = supplierItemTol.TryGetValue((supplierId, itemId.Value), out var st) ? st : null;
            // siTol may itself be null (SupplierItem row exists but value NULL → inherit). itemTol is NOT NULL when
            // the item resolved; if neither resolved, fall back to 0.
            return siTol ?? itemTol ?? 0m;
        }

        // R4 (2026-06-23) — Serial/Lot capture children for these lines (so read/lock view + wizard reload show them).
        var asnLineIds = lineRows.Select(r => r.Id).ToList();
        var serialsByLine = (await db.AsnLineSerials.AsNoTracking()
                .Where(s => asnLineIds.Contains(s.AsnLineId))
                .OrderBy(s => s.SerialNumber)
                .Select(s => new { s.AsnLineId, Dto = new AsnLineSerialDto(s.SerialNumber, s.ExpiryDate, s.ErpCode) })
                .ToListAsync(ct))
            .GroupBy(s => s.AsnLineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AsnLineSerialDto>)g.Select(x => x.Dto).ToList());
        var lotsGrouped = (await db.AsnLineLots.AsNoTracking()
                .Where(l => asnLineIds.Contains(l.AsnLineId))
                .OrderBy(l => l.LotNo)
                .Select(l => new { l.AsnLineId, Dto = new AsnLineLotDto(l.LotNo, l.Qty, l.ExpiryDate, l.ErpCode) })
                .ToListAsync(ct))
            .GroupBy(l => l.AsnLineId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AsnLineLotDto>)g.Select(x => x.Dto).ToList());

        var lines = lineRows.Select(r =>
            {
                // §7.3 / DI-04 — nominal balance + tolerance-adjusted over-ship allowance, computed (never persisted).
                var tolPct = ResolveTolerancePct(r.PoSupplierId, r.PoCompany, r.ItemId, r.ItemCode);
                var balance = Math.Max(0m, r.OrderQty - r.ShippedQtyToDate);
                var overShipAllowance = Policies.OverShipTolerance.RoundAllowance(
                    Math.Max(0m, (r.OrderQty * Policies.OverShipTolerance.Factor(tolPct)) - r.ShippedQtyToDate), rounding);
                return new AsnLineDto(
                    r.Id, r.PurchaseOrderLineId, r.PurchaseOrderId, r.PoNumber,
                    r.PoPositionNo, r.PositionNo, r.SequenceNo,
                    r.ItemCode, r.ItemDescription, r.OrderUnit, r.OrderQty,
                    r.ShippedQty, r.BatchNumber, r.ExpiryDate,
                    serialsByLine.TryGetValue(r.Id, out var sl) ? sl : null,
                    lotsGrouped.TryGetValue(r.Id, out var ll) ? ll : null,
                    r.ShippedQtyToDate, balance, overShipAllowance,
                    r.DeliveryScheduleId, r.ScheduleDeliveryDate);
            })
            .ToList();

        // R6 — ALL generated draft invoices for the ASN (one per (currency, payment-term) group since 0042
        // dropped the unique index). Scalar DraftInvoiceId stays = first for back-compat.
        var draftInvoiceIds = await db.Invoices
            .Where(i => i.AsnId == asnId && !i.IsDeleted)
            .OrderBy(i => i.CreatedOn).ThenBy(i => i.InvoiceNumber)
            .Select(i => i.Id)
            .ToListAsync(ct);
        Guid? draftInvoiceId = draftInvoiceIds.Count > 0 ? draftInvoiceIds[0] : null;

        var attachments = await BuildAttachmentsAsync(db, asnId, ct);

        // R13 — the warehouse grouping key label (replaces ship-to), read off the frozen snapshot the ASN carries —
        // no CompanyAddress join needed. The owned snapshot is auto-materialised with the ASN above; a null
        // WarehouseAddressId yields a null label. The latest approval session is loaded next.
        string? warehouseAddressName = a.WarehouseAddressSnapshot?.AddressName;

        var approval = await db.AsnApprovals.AsNoTracking()
            .Where(ap => ap.AsnId == asnId && !ap.IsDeleted)
            .OrderByDescending(ap => ap.SubmittedOn)
            .Select(ap => new AsnApprovalDto(
                ap.Id, ap.Status.ToString(), ap.SubmittedBy, ap.SubmittedOn,
                ap.DecisionBy, ap.DecisionOn, ap.Reason))
            .FirstOrDefaultAsync(ct);

        // R4 §6.2 — evaluate the ship gate so the UI can disable the gated actions with the reason:
        //  - Draft/Rejected (supplier): Save Changes + Send-For-Approval (or Post, in No-mode);
        //  - PendingApproval (buyer): Approve + Reject;
        //  - Approved (supplier, R14): Save Changes + Post.
        // The same hard block is enforced server-side on each of those paths.
        string? shipBlockReason = null;
        if (a.AsnStatus is AsnStatus.Draft or AsnStatus.Rejected or AsnStatus.PendingApproval or AsnStatus.Approved)
            shipBlockReason = await Policies.AsnDraftGate.EvaluateAsync(db, a.SupplierId, a.Id, coveredPoIds.ToList(), ct);

        // R14 — is Post the supplier's next action? Mirrors AsnLifecycle.AssertCanPost exactly (Approved in both
        // modes; Draft only when confirmation is not required) so the button and the server guard cannot disagree.
        var canPost = a.AsnStatus == AsnStatus.Approved
                      || (!asnConfirmationRequired && a.AsnStatus == AsnStatus.Draft);

        return new AsnDetailDto(
            a.Id, a.Seq, a.AsnNumber,
            a.PurchaseOrderId, headerPoNumber,
            coveredPos,
            a.SupplierId, supplierName,
            a.ExpectedDeliveryDate, a.TimeWindow,
            a.CarrierName, a.TrackingNumber,
            a.VehicleNumber, a.DriverName, a.DriverPhone,
            a.AsnStatus.ToString(), a.Notes,
            a.SubmittedAt, a.SubmittedBy, a.ErpSyncId, a.ErpCode,
            draftInvoiceId, IsLocked(a.AsnStatus),
            lines, attachments,
            // R13 — warehouse address FK + its snapshot label map to the (renamed) DTO WarehouseAddressId/Name slots.
            a.WarehouseAddressId, warehouseAddressName, approval,
            shipBlockReason is not null, shipBlockReason,
            a.InvoiceGenerationStatus, a.InvoiceGenerationNote, draftInvoiceIds,
            // R11 — warehouse (derived, read-only) + the three supplier-entered shipment refs, and createdOn,
            // which the form renders read-only and the LN payload sends as CreateDate.
            a.Warehouse, a.InvoiceNo, a.BillOfLading, a.PackingList, a.CreatedOn,
            // R14 — the shipping date (= post date) + who posted, the supplier's confirmation mode, and whether
            // Post is the next legal action.
            a.PostedAt, a.PostedBy, asnConfirmationRequired, canPost,
            // R16 — the LN ASN_Update verdict (Success | PartialFailure; null = never landed in LN).
            a.ErpStatus);
    }

    public static async Task<IReadOnlyList<DocumentAttachmentDto>> BuildAttachmentsAsync(
        IAppDbContext db, Guid asnId, CancellationToken ct)
    {
        return await db.DocumentUploads
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Asn && d.OwnerEntityId == asnId && !d.IsDeleted)
            .OrderBy(d => d.CreatedOn)
            .Select(d => new DocumentAttachmentDto(
                d.Id, d.FileName, d.MimeType, d.FileSizeKb * 1024L, d.CreatedOn,
                $"files/proxy/{d.Id}", $"files/proxy/{d.Id}"))
            .ToListAsync(ct);
    }
}
