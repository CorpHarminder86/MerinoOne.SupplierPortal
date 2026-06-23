using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
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

    public UpdateAsnCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
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
            .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && lineItemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled })
            .ToListAsync(ct);
        var itemFlags = itemFlagRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

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

        await _db.SaveChangesAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct);
    }
}
