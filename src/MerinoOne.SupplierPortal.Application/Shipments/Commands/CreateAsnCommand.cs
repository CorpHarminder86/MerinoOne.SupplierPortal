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
        });
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
            asn.Lines.Add(new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemId = pol.ItemId,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                PositionNo = pol.PositionNo,     // Addendum A4 — snapshot from the source PO line.
                SequenceNo = pol.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
        }

        _db.Asns.Add(asn);

        await _db.SaveChangesAsync(ct);   // ASN + junction + lines in ONE transaction. NO ERP post (Draft only).

        // The Draft ASN now has a real id; the supplier attaches files directly to it (ownerEntityType='Asn')
        // via the authenticated /document-uploads/attach endpoint while it stays Draft.
        return await AsnDtoBuilder.BuildAsync(_db, asnId, ct);
    }
}
