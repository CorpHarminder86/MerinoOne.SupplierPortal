using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Commands;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PurchaseOrderListItemDto>;

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

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "PurchaseOrder.Read")]
    public async Task<Result<PurchaseOrderDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPurchaseOrderByIdQuery(id), ct);
        return Result<PurchaseOrderDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = "PurchaseOrder.Acknowledge")]
    public async Task<Result> Acknowledge(Guid id, [FromBody] AcknowledgePoRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new AcknowledgePoCommand(id, body ?? new AcknowledgePoRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/accept")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    public async Task<Result> Accept(Guid id, [FromBody] AcceptPoRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new AcceptPoCommand(id, body ?? new AcceptPoRequest(null)), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectPoRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectPoCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/propose-date")]
    [Authorize(Policy = "PurchaseOrder.Accept")]
    public async Task<Result> ProposeDate(Guid id, [FromBody] ProposePoDateRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ProposePoDateCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve-proposal")]
    [Authorize(Policy = "PurchaseOrder.ApproveProposal")]
    public async Task<Result> ApproveProposal(Guid id, [FromBody] ApproveProposalRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveProposalCommand(id, body ?? new ApproveProposalRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
