using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R4 (2026-06-24) — PO Negotiation. Thin REST surface over MediatR (mirrors
/// <see cref="SupplierChangeRequestsController"/>).
///
/// Auth policy split:
/// <list type="bullet">
///   <item><b>PurchaseOrder.Negotiate</b> — supplier raises (create) / withdraws (cancel) a negotiation. The
///         create handler ALSO runs the SupplierWriteGuard (PurchaseOrder.Negotiate permission + a verified
///         SupplierUserMap membership) so the supplier can write THIS portal-originated aggregate without a
///         row-level canWrite grant.</item>
///   <item><b>PurchaseOrder.ApproveNegotiation</b> — buyer approve / reject.</item>
///   <item>List / by-id require <b>PurchaseOrder.Read</b>; seccode RLS scopes a supplier to its own
///         negotiations, a buyer's reviewer queue spans the tenant.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[Route("api/purchase-orders/negotiations")]
public class PoNegotiationsController : ControllerBase
{
    private readonly IMediator _mediator;
    public PoNegotiationsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.PurchaseOrderRead)]
    [EndpointSummary("List PO negotiations")]
    [EndpointDescription(@"Lists PO negotiations visible to the caller.
Filters / params:
- **status**: Optional — Submitted / Approved / Rejected / Cancelled.
Side effects: none. Seccode-scoped — a supplier sees only its own negotiations; a buyer/admin reviewer sees all in the tenant.
Returns: List<PoNegotiationListItemDto> ordered by submittedAt desc. Requires **PurchaseOrder.Read**.")]
    public async Task<Result<List<PoNegotiationListItemDto>>> List([FromQuery] string? status, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPoNegotiationListQuery(status), ct);
        return Result<List<PoNegotiationListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Perm.PurchaseOrderRead)]
    [EndpointSummary("PO negotiation detail")]
    [EndpointDescription(@"Full negotiation detail for the diff view: header + delta lines (original → negotiated qty / delivery date per changed PO line).
Filters / params:
- **id**: Required — negotiation GUID.
Returns: PoNegotiationDto; 404 if not found / not visible (seccode). Requires **PurchaseOrder.Read**.")]
    public async Task<Result<PoNegotiationDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPoNegotiationByIdQuery(id), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Raise a PO negotiation")]
    [EndpointDescription(@"Supplier raises a negotiation proposing revised qty / delivery date on PO lines. The live PO lines are NOT mutated.
Body: CreatePoNegotiationRequest (purchaseOrderId, notes?, lines[]). Each line: purchaseOrderLineId, negotiatedQty (>0), negotiatedDeliveryDate?.
Side effects: only lines that actually differ are persisted (delta); captures PreviousPoStatus; flips PO.PoStatus -> Negotiation; stamps Owner = supplier G-seccode. Authorization: PurchaseOrder.Negotiate permission + verified SupplierUserMap membership (403 otherwise).
Returns: PoNegotiationDto; 404 if PO/supplier not found; 409 if PO not Released/Acknowledged; 400 if no line differs / bad input. Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result<PoNegotiationDto>> Create([FromBody] CreatePoNegotiationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreatePoNegotiationCommand(body), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Cancel a PO negotiation")]
    [EndpointDescription(@"Supplier withdraws an in-flight negotiation (Submitted -> Cancelled). The PO reverts to its captured PreviousPoStatus. Nothing is pushed to ERP.
Filters / params:
- **id**: Required — negotiation GUID.
Returns: empty success; 404 if not found; 409 if not Submitted. Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result> Cancel(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new CancelPoNegotiationCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Perm.PurchaseOrderApproveNegotiation)]
    [EndpointSummary("Approve a PO negotiation")]
    [EndpointDescription(@"Buyer approves a submitted negotiation: negotiation -> Approved, PO.PoStatus -> Approved, and (same transaction) enqueues the ERP round-trip on the outbox (PoNegotiationApprove). The post-commit dispatcher POSTs the negotiated terms + writes the InforSyncLog. Local PO lines are NOT mutated (ERP re-syncs the revised PO inbound).
Filters / params:
- **id**: Required — negotiation GUID.
Returns: empty success; 404 if not found; 409 if not Submitted OR a concurrent approve won the RowVersion race. Requires **PurchaseOrder.ApproveNegotiation**.")]
    public async Task<Result> Approve(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new ApprovePoNegotiationCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Perm.PurchaseOrderApproveNegotiation)]
    [EndpointSummary("Reject a PO negotiation")]
    [EndpointDescription(@"Buyer rejects a submitted negotiation (Submitted -> Rejected, reason stored). The PO reverts to its captured PreviousPoStatus (supplier again sees Ack/Accept/Reject/Negotiate). Nothing is pushed to ERP.
Body: RejectPoNegotiationRequest (reason — required, <=1000).
Returns: empty success; 404 if not found; 409 if not Submitted; 400 if reason missing. Requires **PurchaseOrder.ApproveNegotiation**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectPoNegotiationRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectPoNegotiationCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
