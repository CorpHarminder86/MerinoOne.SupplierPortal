using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Commands;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.AsnListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/asns")]
public class AsnsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AsnsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Asn.Read")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetAsnListQuery(page, pageSize, status, supplierId, purchaseOrderId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Asn.Read")]
    public async Task<Result<AsnDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAsnByIdQuery(id), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Asn.Write")]
    public async Task<Result<AsnDetailDto>> Create([FromBody] CreateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAsnCommand(body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
