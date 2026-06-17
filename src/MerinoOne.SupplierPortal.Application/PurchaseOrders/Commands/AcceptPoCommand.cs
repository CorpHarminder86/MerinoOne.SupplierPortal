using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record AcceptPoCommand(Guid PurchaseOrderId, AcceptPoRequest Body) : IRequest<Unit>;

public class AcceptPoCommandHandler : IRequestHandler<AcceptPoCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;

    public AcceptPoCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
    }

    public async Task<Unit> Handle(AcceptPoCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        if (request.Body.ProposedDate.HasValue)
        {
            po.ProposedDeliveryDate = request.Body.ProposedDate.Value;
            po.PoStatus = PoStatus.DateProposed;
        }
        else
        {
            po.AcceptedAt = DateTime.UtcNow;
            po.PoStatus = PoStatus.Accepted;
        }

        if (!string.IsNullOrWhiteSpace(request.Body.Notes))
            po.Notes = request.Body.Notes;

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
