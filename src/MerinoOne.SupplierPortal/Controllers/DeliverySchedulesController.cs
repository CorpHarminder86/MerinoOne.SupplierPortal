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
    public async Task<Result<DeliveryScheduleDto>> Propose([FromBody] ProposeDeliveryScheduleRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new ProposeDeliveryScheduleCommand(body), ct);
        return Result<DeliveryScheduleDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "DeliverySchedule.Approve")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveDeliveryScheduleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveDeliveryScheduleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
