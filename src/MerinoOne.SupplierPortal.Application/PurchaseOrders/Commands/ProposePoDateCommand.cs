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

/// <summary>
/// Supplier counter-proposes a delivery date (a date-proposal variant of accept). MIGRATED onto the Increment 0
/// outbox: local state change + outbox row in one transaction; the post-commit dispatcher posts the proposed date
/// to LN via <c>AcceptPurchaseOrderAsync(proposedDate)</c>. Gated on <c>poResponseMode = Manual</c>.
/// </summary>
public class ProposePoDateCommandHandler : IRequestHandler<ProposePoDateCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;

    public ProposePoDateCommandHandler(IAppDbContext db, IOutboxDispatcher outbox)
    {
        _db = db; _outbox = outbox;
    }

    public async Task<Unit> Handle(ProposePoDateCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        var mode = await _db.Suppliers.Where(s => s.Id == po.SupplierId)
            .Select(s => s.PoResponseMode).FirstOrDefaultAsync(ct);
        if (mode == PoResponseMode.Auto)
            throw new ConflictException("This supplier is in Auto PO-response mode; PO responses are handled automatically at release.");

        po.ProposedDeliveryDate = request.Body.ProposedDate;
        po.PoStatus = PoStatus.DateProposed;

        // Same op as accept-with-date, so the deterministic key dedupes a re-proposal of the same date.
        var key = OutboxKey.For(OutboxEntity.PurchaseOrder, po.PoNumber, "accept");
        var payload = JsonSerializer.Serialize(new { proposedDate = request.Body.ProposedDate.ToString("o") });
        await _outbox.EnqueueAsync(OutboxTransactionType.PoAccept, OutboxEntity.PurchaseOrder, po.Id, key, payload, ct);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
