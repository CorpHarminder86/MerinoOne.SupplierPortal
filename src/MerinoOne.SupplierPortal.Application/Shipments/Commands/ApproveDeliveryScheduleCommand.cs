using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

public record ApproveDeliveryScheduleCommand(Guid Id, ApproveDeliveryScheduleRequest Body) : IRequest<Unit>;

public class ApproveDeliveryScheduleCommandValidator : AbstractValidator<ApproveDeliveryScheduleCommand>
{
    public ApproveDeliveryScheduleCommandValidator()
    {
        RuleFor(x => x).Must(x => x.Body.Approve || !string.IsNullOrWhiteSpace(x.Body.RejectionReason))
            .WithMessage("RejectionReason is required when rejecting.");
        RuleFor(x => x.Body.RejectionReason).MaximumLength(1000);
    }
}

public class ApproveDeliveryScheduleCommandHandler : IRequestHandler<ApproveDeliveryScheduleCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public ApproveDeliveryScheduleCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(ApproveDeliveryScheduleCommand request, CancellationToken ct)
    {
        var ds = await _db.DeliverySchedules.FirstOrDefaultAsync(d => d.Id == request.Id, ct)
                 ?? throw new NotFoundException("DeliverySchedule", request.Id);

        if (request.Body.Approve)
        {
            ds.ScheduleStatus = ScheduleStatus.Approved;
            ds.ApprovedBy = _user.UserCode;
            ds.RejectionReason = null;
        }
        else
        {
            ds.ScheduleStatus = ScheduleStatus.Rejected;
            ds.RejectionReason = request.Body.RejectionReason;
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
