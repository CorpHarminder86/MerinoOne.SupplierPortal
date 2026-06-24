using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;
using MerinoOne.SupplierPortal.Contracts.Audit;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PurchaseOrderListItemDto>;
using ContractsItemsToDeliverPaged = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.PurchaseOrders.ItemsToDeliverRowDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/purchase-orders")]
public class PurchaseOrdersController : ControllerBase
{
    private readonly IMediator _mediator;
    public PurchaseOrdersController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "PurchaseOrder.Read")]
    [EndpointSummary("Purchase order list")]
    [EndpointDescription(@"Paged list of purchase orders visible to the caller.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — PO lifecycle status (Open / Acknowledged / Accepted / Closed / Cancelled).
- **type**: Optional — PO type filter (e.g. Standard / Blanket / Contract).
- **supplierId**: Optional — restrict to one supplier.
- **search**: Optional — free-text on PO number / reference.
Side effects:
- Seccode-scoped: supplier users see only their own POs.
Returns: PagedResult<PurchaseOrderListItemDto>. Requires permission **PurchaseOrder.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? type = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetPurchaseOrderListQuery(page, pageSize, status, type, supplierId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/history")]
    [Authorize(Policy = "PurchaseOrder.Read")]
    [EndpointSummary("Purchase order change history")]
    [EndpointDescription(@"Field-level audit trail for ONE purchase order — status transitions plus the qty /
delivery-date changes proposed via a PO negotiation. Unlike the generic audit endpoint (Settings.Read, admins
only), this is gated on **PurchaseOrder.Read** and seccode-scoped, so a supplier sees the history of POs it owns
(and only those). Returns: List<AuditEntryDto> newest first; empty if the PO is not visible to the caller.")]
    public async Task<Result<List<AuditEntryDto>>> History(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPurchaseOrderHistoryQuery(id), ct);
        return Result<List<AuditEntryDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("items-to-deliver")]
    [Authorize(Policy = "PurchaseOrder.Read")]
    [EndpointSummary("Items to be delivered")]
    [EndpointDescription(@"Enhancement R4 — Module 8. Open PO lines grouped by (ItemCode, DeliveryDate), netted
against received GRN qty. OpenQty = OrderQty − Σ GoodsReceipt.ReceivedQty. Open-PO statuses: Released,
Acknowledged, Accepted, DateProposed, PartiallyDelivered.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50, max 200).
- **from / to**: Optional — delivery-date range.
- **itemCode**: Optional — restrict to one item.
- **supplierId**: Optional — restrict to one supplier.
Side effects:
- Seccode-scoped: supplier users see only their own open PO lines.
Returns: PagedResult<ItemsToDeliverRowDto>. Requires permission **PurchaseOrder.Read**.")]
    public async Task<Result<ContractsItemsToDeliverPaged>> ItemsToDeliver(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? itemCode = null,
        [FromQuery] Guid? supplierId = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetItemsToDeliverQuery(page, pageSize, from, to, itemCode, supplierId), ct);
        return Result<ContractsItemsToDeliverPaged>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("schedule-calendar")]
    [Authorize(Policy = "PurchaseOrder.Read")]
    [EndpointSummary("PO schedule calendar")]
    [EndpointDescription(@"Enhancement R4 — Module 9. PO-line delivery dates inside a required [from, to] window,
grouped PO-wise per date (one event per (Date, PO) carrying that PO's items + qty).
Filters / params:
- **from**: Required — window start (validated; window <= 366 days).
- **to**: Required — window end (must be >= from).
- **supplierId**: Optional — restrict to one supplier.
Side effects:
- Seccode-scoped: supplier users see only their own deliveries.
Returns: list of PoCalendarEventDto. Requires permission **PurchaseOrder.Read**. 400 if the window is missing/invalid.")]
    public async Task<Result<IReadOnlyList<PoCalendarEventDto>>> ScheduleCalendar(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] Guid? supplierId = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetPoScheduleCalendarQuery(from, to, supplierId), ct);
        return Result<IReadOnlyList<PoCalendarEventDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "PurchaseOrder.Read")]
    [EndpointSummary("Purchase order detail")]
    [EndpointDescription(@"Full PO header + line items + delivery schedule + acknowledgement / proposal trail.
Filters / params:
- **id**: Required — PO GUID.
Returns: PurchaseOrderDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **PurchaseOrder.Read**.")]
    public async Task<Result<PurchaseOrderDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPurchaseOrderByIdQuery(id), ct);
        return Result<PurchaseOrderDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = "PurchaseOrder.Acknowledge")]
    [EndpointSummary("Acknowledge PO")]
    [EndpointDescription(@"Supplier confirms receipt of a new PO (no commitment yet).
Filters / params:
- **id**: Required — PO GUID.
Body:
- **body**: Optional AcknowledgePoRequest with acknowledgement notes.
Side effects:
- Flips status to Acknowledged + stamps acknowledger/timestamp.
Returns: empty success; 404 if not found; 409 if not in acknowledgeable state. Requires permission **PurchaseOrder.Acknowledge**.")]
    public async Task<Result> Acknowledge(Guid id, [FromBody] AcknowledgePoRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new AcknowledgePoCommand(id, body ?? new AcknowledgePoRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/accept")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    [EndpointSummary("Accept PO")]
    [EndpointDescription(@"Supplier commits to PO terms as issued.
Filters / params:
- **id**: Required — PO GUID.
Body:
- **body**: Optional AcceptPoRequest with optional commitment notes.
Side effects:
- Flips status to Accepted + stamps acceptor/timestamp.
- PO becomes eligible for ASN/delivery schedule submission.
Returns: empty success; 404 if not found; 409 if not in acceptable state. Requires permission **PurchaseOrder.Accept**.")]
    public async Task<Result> Accept(Guid id, [FromBody] AcceptPoRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new AcceptPoCommand(id, body ?? new AcceptPoRequest(null)), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    [EndpointSummary("Reject PO")]
    [EndpointDescription(@"Supplier declines a PO; buyer must amend or cancel.
Filters / params:
- **id**: Required — PO GUID.
Body:
- **body**: RejectPoRequest with required reason.
Side effects:
- Flips status to Rejected + records reason + timestamp.
- Notifies the buyer side via configured email template.
Returns: empty success; 404 if not found; 409 if not in rejectable state. Requires permission **PurchaseOrder.Accept**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectPoRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectPoCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/propose-date")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    [EndpointSummary("Propose PO date")]
    [EndpointDescription(@"Supplier counter-proposes a revised delivery date for a PO line.
Filters / params:
- **id**: Required — PO GUID.
Body:
- **body**: ProposePoDateRequest with line reference + proposed date + justification.
Side effects:
- Creates a pending proposal awaiting buyer approval; PO stays in current state.
Returns: empty success; 404 if not found; 409 if PO not in proposable state. Requires permission **PurchaseOrder.Accept**.")]
    public async Task<Result> ProposeDate(Guid id, [FromBody] ProposePoDateRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ProposePoDateCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve-proposal")]
    [Authorize(Policy = "PurchaseOrder.ApproveProposal")]
    [EndpointSummary("Approve PO proposal")]
    [EndpointDescription(@"Buyer approves a supplier-proposed date change.
Filters / params:
- **id**: Required — PO GUID.
Body:
- **body**: Optional ApproveProposalRequest with approver notes.
Side effects:
- Updates the PO line's committed date and clears the pending proposal.
Returns: empty success; 404 if not found; 409 if no open proposal. Requires permission **PurchaseOrder.ApproveProposal**.")]
    public async Task<Result> ApproveProposal(Guid id, [FromBody] ApproveProposalRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveProposalCommand(id, body ?? new ApproveProposalRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
