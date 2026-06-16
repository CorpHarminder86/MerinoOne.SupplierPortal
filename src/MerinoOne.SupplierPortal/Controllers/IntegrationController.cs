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

    [HttpGet("endpoints")]
    [Authorize(Policy = "Integration.Read")]
    [EndpointSummary("Infor endpoints + session telemetry")]
    [EndpointDescription(@"Lists the current tenant's Infor endpoint configuration plus inbound-session liveness telemetry (last received timestamp/status/idempotency-key/message + cumulative received count).
Filters / params:
- **direction**: Optional — filter by ""Inbound"" / ""Outbound"" / ""Bidirectional"".
Returns: List<InforEndpointDto> scoped to the caller's tenant. Requires permission **Integration.Read**.")]
    public async Task<Result<List<InforEndpointDto>>> Endpoints([FromQuery] string? direction, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInforEndpointsQuery(direction), ct);
        return Result<List<InforEndpointDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("endpoints/{id:guid}")]
    [Authorize(Policy = "Integration.Manage")]
    [EndpointSummary("Update Infor endpoint")]
    [EndpointDescription(@"Updates an endpoint's config (URL, BOD name, enabled flag).
Filters / params:
- **id**: Required — endpoint GUID.
Body:
- **body**: UpdateInforEndpointRequest with the endpoint URL, optional BOD name, and the enabled flag.
Returns: empty success; 404 if not found; 400 on validation. Requires permission **Integration.Manage**.")]
    public async Task<Result> UpdateEndpoint(Guid id, [FromBody] UpdateInforEndpointRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateInforEndpointCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("endpoints/{id:guid}/toggle")]
    [Authorize(Policy = "Integration.Manage")]
    [EndpointSummary("Toggle Infor endpoint")]
    [EndpointDescription(@"Flips an endpoint's enabled flag — the inbound kill-switch. When disabled, inbound pushes to that endpoint are rejected with 403.
Filters / params:
- **id**: Required — endpoint GUID.
Returns: the new enabled state (bool); 404 if not found. Requires permission **Integration.Manage**.")]
    public async Task<Result<bool>> ToggleEndpoint(Guid id, CancellationToken ct)
    {
        var enabled = await _mediator.Send(new ToggleInforEndpointCommand(id), ct);
        return Result<bool>.Ok(enabled, HttpContext.TraceIdentifier);
    }
}
