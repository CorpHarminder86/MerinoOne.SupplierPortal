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
    [EndpointSummary("Infor sync log")]
    [EndpointDescription(@"Paged history of Infor inbound/outbound sync runs.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — sync run status (Success / Failed / Partial).
- **entityName**: Optional — filter by entity (e.g. ""Supplier"", ""PurchaseOrder"").
Returns: PagedResult<InforSyncLogDto>. Requires permission **Integration.Read**.")]
    public async Task<Result<PagedResult<InforSyncLogDto>>> SyncLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? entityName = null, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetSyncLogQuery(page, pageSize, status, entityName), ct);
        return Result<PagedResult<InforSyncLogDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("errors")]
    [Authorize(Policy = "Integration.Read")]
    [EndpointSummary("Integration errors")]
    [EndpointDescription(@"Paged list of unresolved + resolved integration errors awaiting operator attention.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **isResolved**: Optional — true to show resolved, false to show outstanding (default both).
Returns: PagedResult<IntegrationErrorDto>. Requires permission **Integration.Read**.")]
    public async Task<Result<PagedResult<IntegrationErrorDto>>> Errors([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] bool? isResolved = null, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetIntegrationErrorsQuery(page, pageSize, isResolved), ct);
        return Result<PagedResult<IntegrationErrorDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("errors/{id:guid}/retry")]
    [Authorize(Policy = "Integration.Manage")]
    [EndpointSummary("Retry integration error")]
    [EndpointDescription(@"Re-queues a failed integration payload for another attempt.
Filters / params:
- **id**: Required — integration error GUID.
Side effects:
- Re-submits the original payload to Infor; on success marks the error resolved.
Returns: empty success; 404 if not found. Requires permission **Integration.Manage**.")]
    public async Task<Result> Retry(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new RetryIntegrationErrorCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
