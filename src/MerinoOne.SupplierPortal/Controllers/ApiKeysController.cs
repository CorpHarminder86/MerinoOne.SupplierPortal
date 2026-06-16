using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.ApiKeys;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Tenant-Admin management of inbound X-APIKey credentials (consumed by Infor LN). Create/rotate/revoke
/// require "Integration.ApiKeys"; listing requires "Integration.Read". The plaintext key is returned ONLY
/// once at create/rotate — list/get never expose the hash or plaintext. Thin — delegates to MediatR.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/api-keys")]
public class ApiKeysController : ControllerBase
{
    private readonly IMediator _mediator;
    public ApiKeysController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Integration.Read")]
    [EndpointSummary("List API keys")]
    [EndpointDescription(@"Lists the current tenant's inbound API keys (metadata only — never the hash or plaintext).
Filters / params:
- **activeOnly**: Optional — true to hide revoked keys. Default false (shows rotation history).
Returns: List<ApiKeyDto>. Requires permission **Integration.Read**.")]
    public async Task<Result<List<ApiKeyDto>>> List([FromQuery] bool activeOnly = false, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetApiKeysQuery(activeOnly), ct);
        return Result<List<ApiKeyDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Integration.ApiKeys")]
    [EndpointSummary("Create API key")]
    [EndpointDescription(@"Mints a new inbound API key bound to a source company + endpoint scopes.
Body:
- **body**: CreateApiKeyRequest with label, bound TenantEntityId, scopes (Integration.Inbound.PaymentTerm / .DeliveryTerm), optional expiry.
Side effects:
- Stores only the key prefix + SHA-256 hash. The plaintext is returned ONCE and is unrecoverable afterward.
Returns: ApiKeySecretDto with the one-time plaintext key; 400 on validation; 404 if the company is unknown. Requires permission **Integration.ApiKeys**.")]
    public async Task<Result<ApiKeySecretDto>> Create([FromBody] CreateApiKeyRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateApiKeyCommand(body), ct);
        return Result<ApiKeySecretDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/rotate")]
    [Authorize(Policy = "Integration.ApiKeys")]
    [EndpointSummary("Rotate API key")]
    [EndpointDescription(@"Rotates a key: mints a successor (same tenant/company/scopes/expiry) and revokes the predecessor.
Filters / params:
- **id**: Required — predecessor key GUID.
Side effects:
- Revokes the predecessor and links it to the successor via ReplacedByApiKeyId.
Returns: ApiKeySecretDto with the successor's one-time plaintext key; 404 if not found. Requires permission **Integration.ApiKeys**.")]
    public async Task<Result<ApiKeySecretDto>> Rotate(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new RotateApiKeyCommand(id), ct);
        return Result<ApiKeySecretDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = "Integration.ApiKeys")]
    [EndpointSummary("Revoke API key")]
    [EndpointDescription(@"Revokes a key immediately — subsequent inbound requests with it return 401.
Filters / params:
- **id**: Required — key GUID.
Side effects:
- Sets IsActive = false + RevokedAt. Idempotent.
Returns: empty success; 404 if not found. Requires permission **Integration.ApiKeys**.")]
    public async Task<Result> Revoke(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new RevokeApiKeyCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
