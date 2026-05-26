using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Commands;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.DeliveryScheduleDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/delivery-schedules")]
public class DeliverySchedulesController : ControllerBase
{
    private readonly IMediator _mediator;
    public DeliverySchedulesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "DeliverySchedule.Read")]
    [EndpointSummary("Delivery schedule list")]
    [EndpointDescription(@"Paged list of supplier-proposed delivery schedules against POs.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — schedule lifecycle status.
- **purchaseOrderId**: Optional — restrict to one PO.
Returns: PagedResult<DeliveryScheduleDto>. Requires permission **DeliverySchedule.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? purchaseOrderId = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetDeliveryScheduleListQuery(page, pageSize, status, purchaseOrderId), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "DeliverySchedule.Propose")]
    [EndpointSummary("Propose delivery schedule")]
    [EndpointDescription(@"Supplier proposes a delivery schedule (date + qty splits) against a PO.
Body:
- **body**: ProposeDeliveryScheduleRequest with PO reference + scheduled dates / quantities.
Side effects:
- Creates the schedule in Proposed status awaiting buyer approval.
Returns: DeliveryScheduleDto on success; 400 on validation; 403 if seccode mismatch. Requires permission **DeliverySchedule.Propose**.")]
    public async Task<Result<DeliveryScheduleDto>> Propose([FromBody] ProposeDeliveryScheduleRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new ProposeDeliveryScheduleCommand(body), ct);
        return Result<DeliveryScheduleDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "DeliverySchedule.Approve")]
    [EndpointSummary("Approve delivery schedule")]
    [EndpointDescription(@"Buyer approves a proposed delivery schedule.
Filters / params:
- **id**: Required — schedule GUID.
- **body**: ApproveDeliveryScheduleRequest with reviewer notes.
Side effects:
- Flips status to Approved + records approver/timestamp.
Returns: empty success; 404 if not found; 409 if not in approvable state. Requires permission **DeliverySchedule.Approve**.")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveDeliveryScheduleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveDeliveryScheduleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
