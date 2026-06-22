using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;

/// <summary>
/// Server-side PO-release hook for suppliers in <c>poResponseMode = Auto</c> (Q8-2). When a PO is released, this
/// command auto-acknowledges it, auto-confirms the delivery date, and ENQUEUES the acceptance to ERP via the
/// Increment 0 outbox (one local transaction + post-commit dispatch — same invariant as the manual path).
///
/// <para><b>FEATURE-FLAGGED</b> behind <c>Features:AutoPoRelease</c> (default OFF). When the flag is off the hook
/// is a no-op so enabling Auto mode is inert until operations switch the flag on.</para>
///
/// <para><b>OPEN QUESTION (release-hook wiring) — flagged to solution-architect, NOT guessed:</b> the COMMAND
/// SURFACE is implemented, but the call site that should INVOKE it on PO release is NOT wired this turn. The portal
/// has no inbound PO-upsert / PO-release command in this increment (PO ingestion is ERP-owned and lives on the
/// inbound integration path not present here). The hook must be invoked from whichever path materialises a PO into
/// <c>PoStatus.Released</c> (inbound PO upsert, or an admin release command). That wiring is deferred until the
/// PO-ingestion path is confirmed — see the hand-off report.</para>
/// </summary>
public record ApplyAutoPoReleaseCommand(Guid PurchaseOrderId) : IRequest<Unit>;

public class ApplyAutoPoReleaseCommandHandler : IRequestHandler<ApplyAutoPoReleaseCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly IOutboxDispatcher _outbox;
    private readonly IConfiguration _config;
    private readonly ILogger<ApplyAutoPoReleaseCommandHandler> _logger;

    public ApplyAutoPoReleaseCommandHandler(
        IAppDbContext db,
        IOutboxDispatcher outbox,
        IConfiguration config,
        ILogger<ApplyAutoPoReleaseCommandHandler> logger)
    {
        _db = db; _outbox = outbox; _config = config; _logger = logger;
    }

    public async Task<Unit> Handle(ApplyAutoPoReleaseCommand request, CancellationToken ct)
    {
        var enabled = bool.TryParse(_config["Features:AutoPoRelease"], out var f) && f;
        if (!enabled)
        {
            _logger.LogDebug("AutoPoRelease feature flag is off; skipping auto-release for PO {PoId}.", request.PurchaseOrderId);
            return Unit.Value;
        }

        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", request.PurchaseOrderId);

        var mode = await _db.Suppliers.Where(s => s.Id == po.SupplierId)
            .Select(s => s.PoResponseMode).FirstOrDefaultAsync(ct);
        if (mode != PoResponseMode.Auto) return Unit.Value;   // Manual suppliers respond through the manual commands.

        var now = DateTime.UtcNow;

        // Auto-acknowledge + auto-confirm the delivery date + auto-accept (Q8-2).
        po.AcknowledgmentAt ??= now;
        po.AcceptedAt = now;
        po.PoStatus = PoStatus.Accepted;

        // ONE deterministic-keyed acceptance enqueued — the post-commit dispatcher posts it to ERP. The accept key
        // dedupes a re-released PO. No LN HTTP inside this txn.
        var key = OutboxKey.For(OutboxEntity.PurchaseOrder, po.TenantId, po.PoNumber, "accept"); // tenant-qualified (review B2)
        await _outbox.EnqueueAsync(OutboxTransactionType.PoAccept, OutboxEntity.PurchaseOrder, po.Id, key, null, ct);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Auto-released PO {PoNumber} (supplier in Auto mode): acknowledged + accepted + acceptance enqueued.", po.PoNumber);
        return Unit.Value;
    }
}
