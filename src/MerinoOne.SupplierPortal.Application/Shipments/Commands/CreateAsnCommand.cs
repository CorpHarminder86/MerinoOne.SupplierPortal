using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

public record CreateAsnCommand(CreateAsnRequest Body) : IRequest<AsnDetailDto>;

public class CreateAsnCommandValidator : AbstractValidator<CreateAsnCommand>
{
    public CreateAsnCommandValidator()
    {
        RuleFor(x => x.Body.PurchaseOrderId).NotEmpty();
        RuleFor(x => x.Body.ExpectedDeliveryDate).NotEmpty();
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
    private readonly IInforIntegrationService _infor;

    public CreateAsnCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
    }

    public async Task<AsnDetailDto> Handle(CreateAsnCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.Body.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.Body.PurchaseOrderId);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == po.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", po.SupplierId);

        // Validate all PO lines belong to this PO
        var requestedLineIds = request.Body.Lines.Select(l => l.PurchaseOrderLineId).ToList();
        var poLineIds = await _db.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == po.Id && requestedLineIds.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var invalid = requestedLineIds.Except(poLineIds).ToList();
        if (invalid.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { $"PurchaseOrderLineId(s) not on PO: {string.Join(", ", invalid)}" }
            });

        var asnId = Guid.NewGuid();
        var asnNumber = $"ASN-{supplier.SupplierCode}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var asn = new Asn
        {
            Id = asnId,
            AsnNumber = asnNumber,
            PurchaseOrderId = po.Id,
            SupplierId = po.SupplierId,
            ExpectedDeliveryDate = request.Body.ExpectedDeliveryDate,
            TimeWindow = request.Body.TimeWindow,
            CarrierName = request.Body.CarrierName,
            TrackingNumber = request.Body.TrackingNumber,
            VehicleNumber = request.Body.VehicleNumber,
            DriverName = request.Body.DriverName,
            DriverPhone = request.Body.DriverPhone,
            Notes = request.Body.Notes,
            AsnStatus = AsnStatus.Submitted,
            SeccodeId = po.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };

        foreach (var line in request.Body.Lines)
        {
            asn.Lines.Add(new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            });
        }

        _db.Asns.Add(asn);

        var sync = await _infor.SubmitAsnAsync(asnId, ct);
        _db.InforSyncLogs.Add(new InforSyncLog
        {
            Id = Guid.NewGuid(),
            EntityName = "Asn",
            Direction = SyncDirection.Outbound,
            Status = sync.Success ? SyncStatus.Success : SyncStatus.Failed,
            IdempotencyKey = sync.IdempotencyKey,
            SyncedAt = DateTime.UtcNow,
            ErrorMessage = sync.Success ? null : sync.Message,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        // Build detail response
        var polById = await _db.PurchaseOrderLines
            .Where(l => requestedLineIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        var lineDtos = asn.Lines
            .OrderBy(l => polById.TryGetValue(l.PurchaseOrderLineId, out var p) ? p.PositionNo : 0)
            .Select(l =>
            {
                var p = polById[l.PurchaseOrderLineId];
                return new AsnLineDto(
                    l.Id, l.PurchaseOrderLineId, p.PositionNo,
                    p.ItemCode, p.ItemDescription, p.OrderUnit, p.OrderQty,
                    l.ShippedQty, l.BatchNumber, l.ExpiryDate);
            }).ToList();

        return new AsnDetailDto(
            asn.Id, asn.Seq, asn.AsnNumber, asn.PurchaseOrderId, po.PoNumber,
            asn.SupplierId, supplier.LegalName,
            asn.ExpectedDeliveryDate, asn.TimeWindow,
            asn.CarrierName, asn.TrackingNumber,
            asn.VehicleNumber, asn.DriverName, asn.DriverPhone,
            asn.AsnStatus.ToString(), asn.Notes,
            lineDtos);
    }
}
