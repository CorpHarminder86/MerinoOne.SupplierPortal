using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3. Creates a <b>Draft</b> ASN. NO ERP post on create (the Increment-0 create-time
/// outbox enqueue is removed — posting happens only on <see cref="SubmitAsnCommand"/>). Supports MULTIPLE POs
/// (Q1): the AsnPurchaseOrder junction is populated from the distinct POs the chosen lines belong to; the legacy
/// scalar PurchaseOrderId is set only for a single-PO ASN (null for multi-PO). Each ASN line snapshots its source
/// PO line's PositionNo/SequenceNo (Addendum A4). Optional deferred-upload attachments are rebound on save.
/// </summary>
public record CreateAsnCommand(CreateAsnRequest Body) : IRequest<AsnDetailDto>;

public class CreateAsnCommandValidator : AbstractValidator<CreateAsnCommand>
{
    public CreateAsnCommandValidator()
    {
        RuleFor(x => x.Body.ExpectedDeliveryDate).NotEmpty();
        RuleFor(x => x.Body)
            .Must(b => (b.PurchaseOrderId.HasValue && b.PurchaseOrderId.Value != Guid.Empty)
                       || (b.PurchaseOrderIds is { Count: > 0 }))
            .WithMessage("At least one PurchaseOrderId is required (PurchaseOrderId or PurchaseOrderIds).")
            .WithName("purchaseOrderId");
        RuleFor(x => x.Body.Lines).NotNull().NotEmpty()
            .WithMessage("At least one ASN line is required.");
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
            line.RuleFor(l => l.ShippedQty).GreaterThan(0).WithMessage("ShippedQty must be greater than 0.");
            // R4 (2026-06-23) — reject duplicate serials / lot numbers WITHIN a line at the input layer so a dup
            // doesn't reach the DB unique index as a 500 (full PO-scope uniqueness + count rules run on Submit).
            line.RuleFor(l => l.Serials).Must(AsnLineRules.SerialsDistinct).WithMessage("Serial numbers must be unique within a line.");
            line.RuleFor(l => l.Lots).Must(AsnLineRules.LotNosDistinct).WithMessage("Lot numbers must be unique within a line.");
        });
    }
}

/// <summary>Shared input-level rules for ASN line serial/lot capture (used by Create + Update validators).</summary>
internal static class AsnLineRules
{
    public static bool SerialsDistinct(List<string>? serials)
    {
        if (serials is null) return true;
        var nonEmpty = serials.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        return nonEmpty.Count == nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public static bool LotNosDistinct(List<AsnLineLotInput>? lots)
    {
        if (lots is null) return true;
        var nonEmpty = lots.Where(l => !string.IsNullOrWhiteSpace(l.LotNo)).Select(l => l.LotNo.Trim()).ToList();
        return nonEmpty.Count == nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }
}

public class CreateAsnCommandHandler : IRequestHandler<CreateAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly Common.Documents.AsnAttachmentRebinder _rebinder;
    private readonly IFulfilmentSettings _fulfilment;

    public CreateAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, Common.Documents.AsnAttachmentRebinder rebinder, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _rebinder = rebinder; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(CreateAsnCommand request, CancellationToken ct)
    {
        var body = request.Body;

        // Resolve the requested PO set (legacy scalar OR explicit list).
        var requestedPoIds = new HashSet<Guid>();
        if (body.PurchaseOrderId is { } pid && pid != Guid.Empty) requestedPoIds.Add(pid);
        if (body.PurchaseOrderIds is { Count: > 0 })
            foreach (var id in body.PurchaseOrderIds) if (id != Guid.Empty) requestedPoIds.Add(id);

        var pos = await _db.PurchaseOrders.Where(p => requestedPoIds.Contains(p.Id)).ToListAsync(ct);
        var missingPos = requestedPoIds.Except(pos.Select(p => p.Id)).ToList();
        if (missingPos.Count > 0)
            throw new NotFoundException("PurchaseOrder", string.Join(", ", missingPos));

        // All POs must belong to ONE supplier (an ASN ships from a single supplier).
        var supplierIds = pos.Select(p => p.SupplierId).Distinct().ToList();
        if (supplierIds.Count != 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["purchaseOrderIds"] = new[] { "All POs on one ASN must belong to the same supplier." }
            });
        var supplierId = supplierIds[0];
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct)
                       ?? throw new NotFoundException("Supplier", supplierId);

        // Load the chosen PO lines, validate each belongs to a PO in the set, and snapshot position/sequence.
        var requestedLineIds = body.Lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        var poLines = await _db.PurchaseOrderLines
            .Where(l => requestedLineIds.Contains(l.Id) && requestedPoIds.Contains(l.PurchaseOrderId))
            .ToDictionaryAsync(l => l.Id, ct);

        var invalid = requestedLineIds.Except(poLines.Keys).ToList();
        if (invalid.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { $"PurchaseOrderLineId(s) not on the supplied PO(s): {string.Join(", ", invalid)}" }
            });

        // R4 (2026-06-23) — Serial/Lot capture: the Item control flags (serialized XOR lot-controlled) decide
        // which child rows to persist per line. Resolve by **ItemCode within the PO's company** (NOT ItemId — the
        // PO line is ERP-fed and routinely has a null ItemId; Item's natural key is (TenantEntityId, Code)).
        // IgnoreQueryFilters — Item is company-scoped and may live in an unshared source company.
        var itemCompany = pos[0].TenantEntityId;
        var lineItemCodes = poLines.Values.Select(l => l.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemFlagRows = await _db.Items.IgnoreQueryFilters()
            // R4 (2026-06-26) — also pull OverShipTolerancePct (the Item-master floor) for the §4.3 atomic guard.
            .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && lineItemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled, i.OverShipTolerancePct })
            .ToListAsync(ct);
        var itemFlags = itemFlagRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

        // R4 (2026-06-26) — Addendum §7.1, Component 4 (Over-Ship Tolerance). Load the SupplierItem overrides for the
        // resolved (supplierId, itemId) pairs so the per-line guard can compute factor = 1 + ResolveOverShipTolerance/100.
        // The PO line's ItemId is often null → resolve via the company+code Item match (flags?.Id) just like serial/lot.
        var resolvedItemIds = poLines.Values
            .Select(l => l.ItemId ?? (!string.IsNullOrWhiteSpace(l.ItemCode) && itemFlags.TryGetValue(l.ItemCode, out var fr) ? fr.Id : (Guid?)null))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var supplierItemTol = (await _db.SupplierItems.IgnoreQueryFilters()
                .Where(si => !si.IsDeleted && si.SupplierId == supplierId && resolvedItemIds.Contains(si.ItemId))
                .Select(si => new { si.ItemId, si.OverShipTolerancePct })
                .ToListAsync(ct))
            .ToDictionary(si => si.ItemId, si => si.OverShipTolerancePct);

        // Resolve the effective over-ship tolerance % for a PO line: SupplierItem override (non-null) ?? Item floor
        // (§7.1/§7.2). NULL SupplierItem row OR no row → inherit the Item floor; an explicit 0 caps at "no over-ship".
        decimal ResolveLineTolerancePct(PurchaseOrderLine pol)
        {
            var flags = !string.IsNullOrWhiteSpace(pol.ItemCode) && itemFlags.TryGetValue(pol.ItemCode, out var f) ? f : null;
            var itemId = pol.ItemId ?? flags?.Id;
            decimal? siTol = itemId is { } id && supplierItemTol.TryGetValue(id, out var st) ? st : null;
            var itemTol = flags?.OverShipTolerancePct ?? 0m;   // Item.* is NOT NULL when resolved; 0 if no Item.
            return siTol ?? itemTol;                            // SupplierItem(non-null) wins; else inherit Item.
        }

        // Aggregate the requested ship qty per PO line — the guard increments once per PO line by the total this ASN
        // adds (a single PO line may appear on more than one requested ASN line in theory; sum so the cumulative is
        // maintained exactly once per line in one statement, never read-then-written).
        var shipQtyByPoLine = body.Lines
            .GroupBy(l => l.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.ShippedQty));

        // D3 — the over-ship CEILING REJECTION is gated by the tenant flag; the cumulative increment ALWAYS runs.
        var enforceGuard = _fulfilment.EnforceOverShipGuard;

        var now = DateTime.UtcNow;
        var asnId = Guid.NewGuid();
        var asnNumber = $"ASN-{supplier.SupplierCode}-{now:yyyyMMddHHmmssfff}";

        // The set of POs actually shipped on (distinct PO of the chosen lines). Single-PO → set the scalar FK
        // for back-compat; multi-PO → leave scalar null, the junction is the source of truth.
        var shippedPoIds = poLines.Values.Select(l => l.PurchaseOrderId).Distinct().ToList();

        var asn = new Asn
        {
            Id = asnId,
            AsnNumber = asnNumber,
            PurchaseOrderId = shippedPoIds.Count == 1 ? shippedPoIds[0] : null,
            SupplierId = supplierId,
            ExpectedDeliveryDate = body.ExpectedDeliveryDate,
            TimeWindow = body.TimeWindow,
            CarrierName = body.CarrierName,
            TrackingNumber = body.TrackingNumber,
            VehicleNumber = body.VehicleNumber,
            DriverName = body.DriverName,
            DriverPhone = body.DriverPhone,
            Notes = body.Notes,
            AsnStatus = AsnStatus.Draft,
            SeccodeId = pos[0].SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };

        // Junction rows for every shipped PO (also for single-PO so the covered-PO list is always complete).
        foreach (var poId in shippedPoIds)
        {
            asn.PurchaseOrders.Add(new AsnPurchaseOrder
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderId = poId,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
        }

        foreach (var line in body.Lines)
        {
            var pol = poLines[line.PurchaseOrderLineId];
            // Resolve the Item by code (the PO line's ItemId is often null) — also used to backfill AsnLine.ItemId.
            var flags = !string.IsNullOrWhiteSpace(pol.ItemCode) && itemFlags.TryGetValue(pol.ItemCode, out var f) ? f : null;
            var asnLine = new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemId = pol.ItemId ?? flags?.Id,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                PositionNo = pol.PositionNo,     // Addendum A4 — snapshot from the source PO line.
                SequenceNo = pol.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };

            // R4 (2026-06-23) — Serial/Lot children. Persist serials only for a serialized item and lots only for
            // a lot-controlled item (the Item XOR guard means at most one applies); the other side is ignored.
            // Draft-stage capture is lenient — full count/uniqueness validation runs on Submit.
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

            asn.Lines.Add(asnLine);
        }

        _db.Asns.Add(asn);

        // R4 (2026-06-23) — rebind any files uploaded DURING creation (ownerEntityType='Staging' under the client's
        // StagingKey) onto this ASN, in the SAME transaction. The rebinder only touches staging rows already stamped
        // with the supplier's seccode at upload time, so cross-supplier keys can't leak in.
        await _rebinder.RebindAsync(body.StagingKey, null, asnId, asn.SeccodeId, now, ct);

        // R4 (2026-06-26) — Addendum §6.2 / §6.5, Component 3 (PO Confirmation Gate). Enforce the ship-gate for
        // EVERY covered PO before any cumulative mutation: a PO that has not reached the supplier's confirmation
        // threshold blocks ASN creation (incl. this Draft save), UNLESS the caller holds PurchaseOrder.OverrideGate
        // and supplied a non-empty OverrideReason — in which case an audited override row is written and shipping
        // proceeds (UC-PO-09). The audit row commits in the SAME SaveChanges below.
        PoConfirmationGateEnforcer.Enforce(
            _db, pos, supplier.PoConfirmationMode, body.OverrideReason, _user, asnId, asnNumber, now);

        // R4 (2026-06-26) — Addendum §4.3 / §9 / DI-02,03 — atomic cumulative-shipped guard. Wrap the per-PO-line
        // cumulative UPDATE(s) and the ASN persist in ONE explicit transaction so a ceiling rejection on any line
        // rolls back every increment already applied (no partial cumulative drift). The cumulative is maintained by
        // a SINGLE conditional ExecuteUpdateAsync per line that reads OrderQty + ShippedQtyToDate LIVE inside the
        // UPDATE — never a C# read-then-write (concurrency-safe per UC-ASN-06, revision-safe per UC-ASN-10/DI-03).
        await using var tx = await _db.BeginTransactionAsync(ct);

        foreach (var (poLineId, shipQty) in shipQtyByPoLine)
        {
            var pol = poLines[poLineId];
            var factor = OverShipTolerance.Factor(ResolveLineTolerancePct(pol));

            int affected;
            if (enforceGuard)
            {
                // Flag ON — single conditional UPDATE: increment ONLY if the tolerance-adjusted ceiling still holds.
                // OrderQty + ShippedQtyToDate are read LIVE inside the statement (revision-safe, concurrency-safe).
                affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId
                                && (l.OrderQty * factor) - l.ShippedQtyToDate >= shipQty)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + shipQty), ct);

                if (affected == 0)
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["shippedQty"] = new[] { "Ship qty exceeds order qty plus over-ship tolerance." }
                    });
            }
            else
            {
                // Flag OFF — plain unconditional increment (NO ceiling rejection). The cumulative is STILL maintained
                // (balance/reconciliation depend on it); only the rejection is gated by the flag (D3).
                affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == poLineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate + shipQty), ct);

                if (affected == 0)
                    throw new NotFoundException("PurchaseOrderLine", poLineId);
            }
        }

        await _db.SaveChangesAsync(ct);   // ASN + junction + lines + rebound attachments. NO ERP post (Draft only).
        await tx.CommitAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asnId, ct);
    }
}
