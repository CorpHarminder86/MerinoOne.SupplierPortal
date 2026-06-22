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
        _db.AsnLines.RemoveRange(existingLines);   // soft-delete via interceptor

        foreach (var line in body.Lines)
        {
            var pol = poLines[line.PurchaseOrderLineId];
            _db.AsnLines.Add(new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asn.Id,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemId = pol.ItemId,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                PositionNo = pol.PositionNo,
                SequenceNo = pol.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
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
