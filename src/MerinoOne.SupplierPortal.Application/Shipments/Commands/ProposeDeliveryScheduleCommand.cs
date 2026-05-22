using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

public record ProposeDeliveryScheduleCommand(ProposeDeliveryScheduleRequest Body) : IRequest<DeliveryScheduleDto>;

public class ProposeDeliveryScheduleCommandValidator : AbstractValidator<ProposeDeliveryScheduleCommand>
{
    public ProposeDeliveryScheduleCommandValidator()
    {
        RuleFor(x => x.Body.PurchaseOrderId).NotEmpty();
        RuleFor(x => x.Body.ProposedDate)
            .GreaterThan(DateTime.UtcNow.Date)
            .WithMessage("ProposedDate must be a future date.");
        RuleFor(x => x.Body.TimeWindow).MaximumLength(50);
        RuleFor(x => x.Body.VehicleInfo).MaximumLength(200);
    }
}

public class ProposeDeliveryScheduleCommandHandler : IRequestHandler<ProposeDeliveryScheduleCommand, DeliveryScheduleDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public ProposeDeliveryScheduleCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<DeliveryScheduleDto> Handle(ProposeDeliveryScheduleCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.Body.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.Body.PurchaseOrderId);

        var ds = new DeliverySchedule
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = po.Id,
            ProposedDate = request.Body.ProposedDate,
            TimeWindow = request.Body.TimeWindow,
            VehicleInfo = request.Body.VehicleInfo,
            ScheduleStatus = ScheduleStatus.Proposed,
            SeccodeId = po.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.DeliverySchedules.Add(ds);
        await _db.SaveChangesAsync(ct);

        return new DeliveryScheduleDto(
            ds.Id, ds.Seq, ds.PurchaseOrderId, po.PoNumber,
            ds.ProposedDate, ds.TimeWindow, ds.VehicleInfo,
            ds.ScheduleStatus.ToString(), ds.ApprovedBy, ds.RejectionReason, ds.CreatedOn);
    }
}
