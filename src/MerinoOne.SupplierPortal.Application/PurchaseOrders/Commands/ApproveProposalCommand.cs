using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record ApproveProposalCommand(Guid PurchaseOrderId, ApproveProposalRequest Body) : IRequest<Unit>;

public class ApproveProposalCommandHandler : IRequestHandler<ApproveProposalCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;

    public ApproveProposalCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
    }

    public async Task<Unit> Handle(ApproveProposalCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        if (po.PoStatus != PoStatus.DateProposed)
            throw new ConflictException("Only DateProposed purchase orders can have their proposal approved.");

        po.PoStatus = PoStatus.Accepted;
        po.AcceptedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Body.Comment))
            po.Notes = string.IsNullOrEmpty(po.Notes) ? request.Body.Comment : po.Notes + "\n" + request.Body.Comment;

        var sync = await _infor.AcceptPurchaseOrderAsync(po.Id, po.ProposedDeliveryDate, ct);
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
