using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
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

    public CreateAsnCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
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
            .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && lineItemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled })
            .ToListAsync(ct);
        var itemFlags = itemFlagRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

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

        await _db.SaveChangesAsync(ct);   // ASN + junction + lines in ONE transaction. NO ERP post (Draft only).

        // The Draft ASN now has a real id; the supplier attaches files directly to it (ownerEntityType='Asn')
        // via the authenticated /document-uploads/attach endpoint while it stays Draft.
        return await AsnDtoBuilder.BuildAsync(_db, asnId, ct);
    }
}
