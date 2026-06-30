using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3. Edits a <b>Draft</b> ASN only — rejected (409) once Submitted/Cancelled
/// ("lock everything on submit"). Replaces the line set (re-snapshotting each line's PO PositionNo/SequenceNo)
/// and rebuilds the multi-PO junction from the lines' distinct POs.
/// </summary>
public record UpdateAsnCommand(Guid Id, UpdateAsnRequest Body) : IRequest<AsnDetailDto>;

public class UpdateAsnCommandValidator : AbstractValidator<UpdateAsnCommand>
{
    public UpdateAsnCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.ExpectedDeliveryDate).NotEmpty();
        RuleFor(x => x.Body.Lines).NotNull().NotEmpty().WithMessage("At least one ASN line is required.");
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
            line.RuleFor(l => l.ShippedQty).GreaterThan(0).WithMessage("ShippedQty must be greater than 0.");
            // R4 (2026-06-23) — reject duplicate serials / lot numbers within a line at the input layer (see CreateAsn).
            line.RuleFor(l => l.Serials).Must(AsnLineRules.SerialsDistinct).WithMessage("Serial numbers must be unique within a line.");
            line.RuleFor(l => l.Lots).Must(AsnLineRules.LotNosDistinct).WithMessage("Lot numbers must be unique within a line.");
        });
    }
}

public class UpdateAsnCommandHandler : IRequestHandler<UpdateAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IFulfilmentSettings _fulfilment;

    public UpdateAsnCommandHandler(IAppDbContext db, ICurrentUser user, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(UpdateAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        if (asn.AsnStatus != AsnStatus.Draft)
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; only Draft ASNs can be edited.");

        var body = request.Body;
        var now = DateTime.UtcNow;

        // Resolve the new line set — every line must belong to a PO owned by this ASN's supplier.
        var requestedLineIds = body.Lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        var poLines = await (from pol in _db.PurchaseOrderLines
                             join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                             where requestedLineIds.Contains(pol.Id) && po.SupplierId == asn.SupplierId
                             select pol).ToDictionaryAsync(l => l.Id, ct);

        var invalid = requestedLineIds.Except(poLines.Keys).ToList();
        if (invalid.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { $"PurchaseOrderLineId(s) not on a PO for this supplier: {string.Join(", ", invalid)}" }
            });

        // R4 (2026-06-23) — Serial/Lot capture: the Item control flags (serialized XOR lot-controlled) decide which
        // child rows to persist per replaced line. Resolve by **ItemCode within the ASN's company** (NOT ItemId —
        // the PO line is ERP-fed and routinely has a null ItemId; Item natural key = (TenantEntityId, Code)).
        // IgnoreQueryFilters — Item is company-scoped.
        var itemCompany = asn.TenantEntityId;
        var lineItemCodes = poLines.Values.Select(l => l.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemFlagRows = await _db.Items.IgnoreQueryFilters()
            // R4 (2026-06-26) — also pull OverShipTolerancePct for the §4.3 delta guard.
            .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && lineItemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled, i.OverShipTolerancePct })
            .ToListAsync(ct);
        var itemFlags = itemFlagRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

        // R4 (2026-06-26) — Addendum §7.1: SupplierItem overrides for the resolved (supplierId, itemId) pairs so the
        // delta guard can compute the tolerance-adjusted ceiling for any POSITIVE net change to a PO line's ship qty.
        var resolvedItemIds = poLines.Values
            .Select(l => l.ItemId ?? (!string.IsNullOrWhiteSpace(l.ItemCode) && itemFlags.TryGetValue(l.ItemCode, out var fr) ? fr.Id : (Guid?)null))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var supplierItemTol = (await _db.SupplierItems.IgnoreQueryFilters()
                .Where(si => !si.IsDeleted && si.SupplierId == asn.SupplierId && resolvedItemIds.Contains(si.ItemId))
                .Select(si => new { si.ItemId, si.OverShipTolerancePct })
                .ToListAsync(ct))
            .ToDictionary(si => si.ItemId, si => si.OverShipTolerancePct);

        decimal ResolveLineTolerancePct(PurchaseOrderLine pol)
        {
            var flags = !string.IsNullOrWhiteSpace(pol.ItemCode) && itemFlags.TryGetValue(pol.ItemCode, out var f) ? f : null;
            var itemId = pol.ItemId ?? flags?.Id;
            decimal? siTol = itemId is { } id && supplierItemTol.TryGetValue(id, out var st) ? st : null;
            var itemTol = flags?.OverShipTolerancePct ?? 0m;
            return siTol ?? itemTol;   // SupplierItem(non-null) wins; else inherit Item floor.
        }

        // Header fields.
        asn.ExpectedDeliveryDate = body.ExpectedDeliveryDate;
        asn.TimeWindow = body.TimeWindow;
        asn.CarrierName = body.CarrierName;
        asn.TrackingNumber = body.TrackingNumber;
        asn.VehicleNumber = body.VehicleNumber;
        asn.DriverName = body.DriverName;
        asn.DriverPhone = body.DriverPhone;
        asn.Notes = body.Notes;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // Replace lines (soft-delete the old, insert the new — re-snapshotting position/sequence).
        var existingLines = await _db.AsnLines.Where(l => l.AsnId == asn.Id).ToListAsync(ct);

        // R4 (2026-06-26) — Addendum §4.3 — cumulative DELTA. A Draft edit fully replaces the line set, so the
        // correct adjustment to each PO line's ShippedQtyToDate is the NET change: Σ(new ship qty for that PO line)
        // − Σ(old ship qty for that PO line). The old contributions are captured HERE before the replace; the new
        // contributions are summed from body.Lines. delta>0 may need the flag-gated ceiling check; delta<0 (or =0)
        // is a plain decrement / no-op. The cumulative is adjusted with a single ExecuteUpdateAsync per PO line in
        // the transaction below — never a C# read-then-write (DI-02).
        var oldQtyByPoLine = existingLines
            .GroupBy(l => l.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ShippedQty));

        // R4 (2026-06-23) — Serial/Lot capture: also replace the children of the lines being removed. Soft-delete
        // the existing serial/lot rows for the old lines so the new capture set fully supersedes the old one.
        var existingLineIds = existingLines.Select(l => l.Id).ToList();
        if (existingLineIds.Count > 0)
        {
            var oldSerials = await _db.AsnLineSerials.Where(s => existingLineIds.Contains(s.AsnLineId)).ToListAsync(ct);
            _db.AsnLineSerials.RemoveRange(oldSerials);   // soft-delete via interceptor
            var oldLots = await _db.AsnLineLots.Where(l => existingLineIds.Contains(l.AsnLineId)).ToListAsync(ct);
            _db.AsnLineLots.RemoveRange(oldLots);         // soft-delete via interceptor
        }

        _db.AsnLines.RemoveRange(existingLines);   // soft-delete via interceptor

        foreach (var line in body.Lines)
        {
            var pol = poLines[line.PurchaseOrderLineId];
            // Resolve the Item by code (the PO line's ItemId is often null) — also backfills AsnLine.ItemId.
            var flags = !string.IsNullOrWhiteSpace(pol.ItemCode) && itemFlags.TryGetValue(pol.ItemCode, out var f) ? f : null;
            var asnLine = new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asn.Id,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemId = pol.ItemId ?? flags?.Id,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                PositionNo = pol.PositionNo,
                SequenceNo = pol.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };

            // R4 (2026-06-23) — Serial/Lot children for the new line. Serials only for a serialized item, lots
            // only for a lot-controlled item (Item XOR guard); the other side is ignored. Submit re-validates.
            if (flags?.IsSerialized == true && line.Serials is { Count: > 0 })
            {
                foreach (var serial in line.Serials.Where(s => !string.IsNullOrWhiteSpace(s)))
                    asnLine.Serials.Add(new AsnLineSerial
                    {
                        Id = Guid.NewGuid(),
                        AsnLineId = asnLine.Id,
                        SerialNumber = serial.Trim(),
                        CreatedBy = _user.UserCode,
                        CreatedOn = now,
                    });
            }
            else if (flags?.IsLotControlled == true && line.Lots is { Count: > 0 })
            {
                foreach (var lot in line.Lots.Where(l => !string.IsNullOrWhiteSpace(l.LotNo)))
                    asnLine.Lots.Add(new AsnLineLot
                    {
                        Id = Guid.NewGuid(),
                        AsnLineId = asnLine.Id,
                        LotNo = lot.LotNo.Trim(),
                        Qty = lot.Qty,
                        ExpiryDate = lot.ExpiryDate,
                        CreatedBy = _user.UserCode,
                        CreatedOn = now,
                    });
            }

            _db.AsnLines.Add(asnLine);
        }

        // Rebuild the junction from the new lines' distinct POs.
        var newPoIds = poLines.Values.Select(l => l.PurchaseOrderId).Distinct().ToList();
        var existingJunction = await _db.AsnPurchaseOrders.Where(j => j.AsnId == asn.Id).ToListAsync(ct);
        _db.AsnPurchaseOrders.RemoveRange(existingJunction);
        foreach (var poId in newPoIds)
        {
            _db.AsnPurchaseOrders.Add(new AsnPurchaseOrder
            {
                Id = Guid.NewGuid(),
                AsnId = asn.Id,
                PurchaseOrderId = poId,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
        }
        asn.PurchaseOrderId = newPoIds.Count == 1 ? newPoIds[0] : null;

        // R4 (2026-06-26) — apply the cumulative DELTA per PO line, then persist, in ONE transaction so a ceiling
        // rejection rolls back every delta already applied. Net delta = new − old over the union of touched PO lines.
        var newQtyByPoLine = body.Lines
            .GroupBy(l => l.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ShippedQty));
        var deltaPoLineIds = oldQtyByPoLine.Keys.Union(newQtyByPoLine.Keys).Distinct().ToList();
        var enforceGuard = _fulfilment.EnforceOverShipGuard;

        await using var tx = await _db.BeginTransactionAsync(ct);

        foreach (var poLineId in deltaPoLineIds)
        {
            var oldQty = oldQtyByPoLine.TryGetValue(poLineId, out var o) ? o : 0m;
            var newQty = newQtyByPoLine.TryGetValue(poLineId, out var n) ? n : 0m;
            var delta = newQty - oldQty;
            if (delta == 0m) continue;   // no net change for this PO line.

            if (delta > 0m && enforceGuard)
            {
                // Net INCREASE with the guard ON — the ceiling must still hold for the added amount. Reading OrderQty
                // + ShippedQtyToDate LIVE: removing the old contribution and adding the new is equivalent to testing
                // OrderQty*factor − ShippedQtyToDate >= delta (single conditional UPDATE; revision/concurrency-safe).
                var pol = poLines[poLineId];
                var factor = OverShipTolerance.Factor(ResolveLineTolerancePct(pol));

                // R4 (2026-06-30) — rounded-allowance consistency (mirrors CreateAsnCommand). When a rounding mode is
                // active, reject the net increase against the SAME rounded cap the DTO/client showed. The live-SQL
                // ceiling below stays UNROUNDED (non-linear flooring isn't SQL-translatable) as the concurrency-safe net.
                var rounding = _fulfilment.OverShipAllowanceRounding;
                if (rounding != OverShipRoundingMode.None)
                {
                    var roundedCap = OverShipTolerance.RoundAllowance(
                        Math.Max(0m, (pol.OrderQty * factor) - pol.ShippedQtyToDate), rounding);
                    if (delta > roundedCap)
                        throw new ValidationException(new Dictionary<string, string[]>
                        {
                            ["shippedQty"] = new[] { "Ship qty exceeds order qty plus over-ship tolerance." }
                        });
                }

                var affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId
                                && (l.OrderQty * factor) - l.ShippedQtyToDate >= delta)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + delta), ct);

                if (affected == 0)
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["shippedQty"] = new[] { "Ship qty exceeds order qty plus over-ship tolerance." }
                    });
            }
            else
            {
                // Net DECREASE (plain decrement) OR a net increase with the guard OFF (unconditional add). Either way
                // the cumulative is maintained by a single ExecuteUpdateAsync (positive or negative delta).
                await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + delta), ct);
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
    }
}
