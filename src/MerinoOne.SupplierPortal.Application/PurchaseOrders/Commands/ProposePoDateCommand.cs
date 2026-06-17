using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record ProposePoDateCommand(Guid PurchaseOrderId, ProposePoDateRequest Body) : IRequest<Unit>;

public class ProposePoDateCommandValidator : AbstractValidator<ProposePoDateCommand>
{
    public ProposePoDateCommandValidator()
    {
        RuleFor(x => x.Body.ProposedDate)
            .GreaterThan(DateTime.UtcNow.Date)
            .WithMessage("ProposedDate must be a future date.");
    }
}

public class ProposePoDateCommandHandler : IRequestHandler<ProposePoDateCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;

    public ProposePoDateCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
    }

    public async Task<Unit> Handle(ProposePoDateCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        po.ProposedDeliveryDate = request.Body.ProposedDate;
        po.PoStatus = PoStatus.DateProposed;

        var sync = await _infor.AcceptPurchaseOrderAsync(po.Id, request.Body.ProposedDate, ct);
        _db.InforSyncLogs.Add(new InforSyncLog
        {
            Id = Guid.NewGuid(),
            EntityName = "PurchaseOrder",
            EntityId = po.Id.ToString(),
            Direction = SyncDirection.Outbound,
            Status = sync.Success ? SyncStatus.Success : SyncStatus.Failed,
            IdempotencyKey = sync.IdempotencyKey,
            SyncedAt = DateTime.UtcNow,
            ErrorMessage = sync.Success ? null : sync.Message,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
