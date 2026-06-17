using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record AcknowledgePoCommand(Guid PurchaseOrderId, AcknowledgePoRequest Body) : IRequest<Unit>;

public class AcknowledgePoCommandHandler : IRequestHandler<AcknowledgePoCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;

    public AcknowledgePoCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
    }

    public async Task<Unit> Handle(AcknowledgePoCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        // Idempotent: if already acknowledged or further along, leave as-is.
        if (po.PoStatus == PoStatus.Released)
        {
            po.PoStatus = PoStatus.Acknowledged;
            po.AcknowledgmentAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.Body.Notes))
                po.Notes = request.Body.Notes;
        }
        else if (po.AcknowledgmentAt == null)
        {
            po.AcknowledgmentAt = DateTime.UtcNow;
        }

        var sync = await _infor.AcknowledgePurchaseOrderAsync(po.Id, ct);
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
