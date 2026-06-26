using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

public record AcceptPoCommand(Guid PurchaseOrderId, AcceptPoRequest Body) : IRequest<Unit>;

/// <summary>
/// PO acceptance (supplier). MIGRATED onto the Increment 0 outbox: the local PO state change + an
/// <c>OutboxMessage</c> (deterministic key) commit in ONE <c>SaveChangesAsync</c>; the post-commit dispatcher
/// performs the LN post. No LN HTTP call inside the unit of work (fixes D1), key reused across retries (D2),
/// failures become retryable IntegrationErrors via the dispatcher (D3).
///
/// R4 (2026-06-26) — D1/D2: ACCEPT IS ACCEPT-ONLY now (the old "accept with an alternate/proposed date" branch
/// is retired — all counter-proposals flow through the PurchaseOrderNegotiation aggregate). For
/// <c>PoConfirmationMode.AutoAccept</c> suppliers the portal auto-stamps Accepted at PO release (server-side hook),
/// so a manual accept is rejected (409); AcceptToShip / AcknowledgeToShip suppliers accept here manually.
/// </summary>
public class AcceptPoCommandHandler : IRequestHandler<AcceptPoCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;

    public AcceptPoCommandHandler(IAppDbContext db, IOutboxDispatcher outbox)
    {
        _db = db; _outbox = outbox;
    }

    public async Task<Unit> Handle(AcceptPoCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        var mode = await _db.Suppliers.Where(s => s.Id == po.SupplierId)
            .Select(s => s.PoConfirmationMode).FirstOrDefaultAsync(ct);
        if (mode == PoConfirmationMode.AutoAccept)
            throw new ConflictException("This supplier is in AutoAccept confirmation mode; PO acceptance is handled automatically at release.");

        po.AcceptedAt = DateTime.UtcNow;
        po.PoStatus = PoStatus.Accepted;

        if (!string.IsNullOrWhiteSpace(request.Body.Notes))
            po.Notes = request.Body.Notes;

        // Deterministic key — REUSED across retries so LN dedupes (doubles as the ERP correlation id / portalRef).
        // Tenant-qualified (review B2): PoNumber is unique within the tenant; the key must be tenant-unique.
        // R4 (2026-06-26) — D2: accept is accept-only (no proposed-date payload).
        var key = OutboxKey.For(OutboxEntity.PurchaseOrder, po.TenantId, po.PoNumber, "accept");
        await _outbox.EnqueueAsync(OutboxTransactionType.PoAccept, OutboxEntity.PurchaseOrder, po.Id, key, null, ct);

        await _db.SaveChangesAsync(ct);   // PO state + outbox row in one transaction; dispatch is post-commit.
        return Unit.Value;
    }
}
