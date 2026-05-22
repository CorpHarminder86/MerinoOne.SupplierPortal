using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Commands;
using MerinoOne.SupplierPortal.Application.Integration.Queries;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/integration")]
public class IntegrationController : ControllerBase
{
    private readonly IMediator _mediator;
    public IntegrationController(IMediator mediator) => _mediator = mediator;

    [HttpGet("sync-log")]
    [Authorize(Policy = "Integration.Read")]
    public async Task<Result<PagedResult<InforSyncLogDto>>> SyncLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? entityName = null, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetSyncLogQuery(page, pageSize, status, entityName), ct);
        return Result<PagedResult<InforSyncLogDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("errors")]
    [Authorize(Policy = "Integration.Read")]
    public async Task<Result<PagedResult<IntegrationErrorDto>>> Errors([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] bool? isResolved = null, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetIntegrationErrorsQuery(page, pageSize, isResolved), ct);
        return Result<PagedResult<IntegrationErrorDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("errors/{id:guid}/retry")]
    [Authorize(Policy = "Integration.Manage")]
    public async Task<Result> Retry(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new RetryIntegrationErrorCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
