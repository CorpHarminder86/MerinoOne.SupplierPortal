using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Suppliers.Commands;
using MerinoOne.SupplierPortal.Application.Suppliers.Queries;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly IMediator _mediator;
    public SuppliersController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<Result<List<SupplierListItemDto>>> List([FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierListQuery(status, search), ct);
        return Result<List<SupplierListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    public async Task<Result<SupplierDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierByIdQuery(id), ct);
        return Result<SupplierDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/verify-nic")]
    public async Task<Result<List<SupplierVerificationDto>>> VerifyNic(Guid id, [FromBody] VerifyNicRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new VerifyNicCommand(id, body), ct);
        return Result<List<SupplierVerificationDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/verifications")]
    public async Task<Result<List<SupplierVerificationDto>>> Verifications(Guid id, CancellationToken ct)
    {
        var detail = await _mediator.Send(new GetSupplierByIdQuery(id), ct);
        return Result<List<SupplierVerificationDto>>.Ok(detail.Verifications, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
