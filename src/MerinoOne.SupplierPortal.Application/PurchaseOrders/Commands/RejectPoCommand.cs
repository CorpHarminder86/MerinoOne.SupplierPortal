using System.Text.Json;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record RejectPoCommand(Guid PurchaseOrderId, RejectPoRequest Body) : IRequest<Unit>;

public class RejectPoCommandValidator : AbstractValidator<RejectPoCommand>
{
    public RejectPoCommandValidator()
    {
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000);
    }
}

/// <summary>
/// PO rejection (supplier). MIGRATED onto the Increment 0 outbox: local state change + outbox row in one
/// transaction; the post-commit dispatcher posts the rejection (with reason) to LN. Gated on <c>poResponseMode = Manual</c>.
/// </summary>
public class RejectPoCommandHandler : IRequestHandler<RejectPoCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;

    public RejectPoCommandHandler(IAppDbContext db, IOutboxDispatcher outbox)
    {
        _db = db; _outbox = outbox;
    }

    public async Task<Unit> Handle(RejectPoCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        var mode = await _db.Suppliers.Where(s => s.Id == po.SupplierId)
            .Select(s => s.PoResponseMode).FirstOrDefaultAsync(ct);
        if (mode == PoResponseMode.Auto)
            throw new ConflictException("This supplier is in Auto PO-response mode; PO responses are handled automatically at release.");

        po.PoStatus = PoStatus.Rejected;
        po.RejectionReason = request.Body.Reason;

        var key = OutboxKey.For(OutboxEntity.PurchaseOrder, po.PoNumber, "reject");
        var payload = JsonSerializer.Serialize(new { reason = request.Body.Reason });
        await _outbox.EnqueueAsync(OutboxTransactionType.PoReject, OutboxEntity.PurchaseOrder, po.Id, key, payload, ct);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
