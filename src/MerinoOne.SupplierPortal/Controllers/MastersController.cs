using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Commands;
using MerinoOne.SupplierPortal.Application.Masters.Queries;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/masters")]
public class MastersController : ControllerBase
{
    private readonly IMediator _mediator;
    public MastersController(IMediator mediator) => _mediator = mediator;

    // ---------------- Delivery Terms ----------------

    [HttpGet("delivery-terms")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<List<MasterItemDto>>> ListDeliveryTerms([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDeliveryTermsQuery(isActive), ct);
        return Result<List<MasterItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("delivery-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<MasterItemDto>> GetDeliveryTerm(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDeliveryTermByIdQuery(id), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<MasterItemDto>> CreateDeliveryTerm([FromBody] CreateDeliveryTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateDeliveryTermCommand(body), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("delivery-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<MasterItemDto>> UpdateDeliveryTerm(Guid id, [FromBody] UpdateDeliveryTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateDeliveryTermCommand(id, body), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result> DeactivateDeliveryTerm(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateDeliveryTermCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Payment Terms ----------------

    [HttpGet("payment-terms")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<List<PaymentTermDto>>> ListPaymentTerms([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentTermsQuery(isActive), ct);
        return Result<List<PaymentTermDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("payment-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<PaymentTermDto>> GetPaymentTerm(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentTermByIdQuery(id), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("payment-terms")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<PaymentTermDto>> CreatePaymentTerm([FromBody] CreatePaymentTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreatePaymentTermCommand(body), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("payment-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<PaymentTermDto>> UpdatePaymentTerm(Guid id, [FromBody] UpdatePaymentTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdatePaymentTermCommand(id, body), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("payment-terms/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result> DeactivatePaymentTerm(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivatePaymentTermCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Items ----------------

    [HttpGet("items")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<List<ItemDto>>> ListItems([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetItemsQuery(isActive, search), ct);
        return Result<List<ItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("items/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<ItemDto>> GetItem(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetItemByIdQuery(id), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("items")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<ItemDto>> CreateItem([FromBody] CreateItemRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateItemCommand(body), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("items/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<ItemDto>> UpdateItem(Guid id, [FromBody] UpdateItemRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateItemCommand(id, body), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("items/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result> DeactivateItem(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateItemCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
