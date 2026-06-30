using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record AcknowledgePoCommand(Guid PurchaseOrderId, AcknowledgePoRequest Body) : IRequest<Unit>;

/// <summary>
/// PO acknowledgement (supplier). MIGRATED onto the Increment 0 outbox: local state change + outbox row in one
/// transaction; the post-commit dispatcher posts the acknowledgement to LN. R4 (2026-06-26) — D1: blocked for
/// <c>PoConfirmationMode.AutoAccept</c> suppliers (auto-handled at release); AcceptToShip / AcknowledgeToShip
/// suppliers acknowledge here manually.
/// </summary>
public class AcknowledgePoCommandHandler : IRequestHandler<AcknowledgePoCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;
    private readonly DeliverySchedules.DeliveryScheduleFactory _schedules;

    public AcknowledgePoCommandHandler(IAppDbContext db, IOutboxDispatcher outbox, DeliverySchedules.DeliveryScheduleFactory schedules)
    {
        _db = db; _outbox = outbox; _schedules = schedules;
    }

    public async Task<Unit> Handle(AcknowledgePoCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        var mode = await _db.Suppliers.Where(s => s.Id == po.SupplierId)
            .Select(s => s.PoConfirmationMode).FirstOrDefaultAsync(ct);
        if (mode == PoConfirmationMode.AutoAccept)
            throw new ConflictException("This supplier is in AutoAccept confirmation mode; PO acknowledgement is handled automatically at release.");

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

        var key = OutboxKey.For(OutboxEntity.PurchaseOrder, po.TenantId, po.PoNumber, "acknowledge"); // tenant-qualified (review B2)
        await _outbox.EnqueueAsync(OutboxTransactionType.PoAcknowledge, OutboxEntity.PurchaseOrder, po.Id, key, null, ct);

        // R5 (§8.1) — AcknowledgeToShip becomes shippable ON Acknowledged: stage the per-line delivery schedules so
        // they commit in the SAME transaction. Idempotent — safe even when this is a no-op re-acknowledge.
        await _schedules.EnsureDeliverySchedulesAsync(po.Id, ct);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
