using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Invoices.Commands;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Invoices.InvoiceListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly IMediator _mediator;
    public InvoicesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Invoice.Read")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetInvoiceListQuery(page, pageSize, status, supplierId, purchaseOrderId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Invoice.Read")]
    public async Task<Result<InvoiceDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInvoiceByIdQuery(id), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Invoice.Submit")]
    public async Task<Result<InvoiceDetailDto>> Create([FromBody] CreateInvoiceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateInvoiceCommand(body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = "Invoice.Review")]
    public async Task<Result> Review(Guid id, [FromBody] ReviewInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ReviewInvoiceCommand(id, body ?? new ReviewInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "Invoice.Approve")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveInvoiceCommand(id, body ?? new ApproveInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "Invoice.Approve")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectInvoiceRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectInvoiceCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
