using MediatR;
using MerinoOne.SupplierPortal.Application.Audit.Queries;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Audit;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

/// <summary>
/// PO-scoped audit trail for the PO detail "History" tab. The generic audit endpoint is admin-only
/// (<c>Settings.Read</c>); this one is gated on <c>PurchaseOrder.Read</c> so the SUPPLIER who owns the PO can see
/// its change history — including the qty / delivery-date changes proposed via a negotiation
/// ([[PoNegotiationHistory]] writes those as PurchaseOrder-targeted audit rows).
///
/// <para><b>Scoping (no IDOR):</b> the PO is first resolved through the RLS-scoped <see cref="IAppDbContext"/>, so a
/// caller who cannot see the PO (different seccode / company / tenant) gets an EMPTY trail and never another PO's
/// audit. Only after that gate do we fetch the (tenant-scoped) audit rows via <see cref="GetAuditTrailQuery"/>.
/// This deliberately does NOT widen the generic all-entity audit endpoint to suppliers.</para>
/// </summary>
public record GetPurchaseOrderHistoryQuery(Guid PurchaseOrderId) : IRequest<List<AuditEntryDto>>;

public class GetPurchaseOrderHistoryQueryHandler : IRequestHandler<GetPurchaseOrderHistoryQuery, List<AuditEntryDto>>
{
    private readonly IAppDbContext _db;
    private readonly IMediator _mediator;

    public GetPurchaseOrderHistoryQueryHandler(IAppDbContext db, IMediator mediator)
    {
        _db = db;
        _mediator = mediator;
    }

    public async Task<List<AuditEntryDto>> Handle(GetPurchaseOrderHistoryQuery request, CancellationToken ct)
    {
        // RLS-scoped existence check: the global query filters (seccode / company / tenant) mean a supplier only
        // "sees" their own POs here — anyone else gets an empty trail, never a foreign PO's audit.
        var canSee = await _db.PurchaseOrders.AnyAsync(p => p.Id == request.PurchaseOrderId, ct);
        if (!canSee)
            return new List<AuditEntryDto>();

        return await _mediator.Send(new GetAuditTrailQuery("PurchaseOrder", request.PurchaseOrderId), ct);
    }
}
