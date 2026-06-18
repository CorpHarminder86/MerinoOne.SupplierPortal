using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Inbound master-data ingestion consumed by Infor LN. Authenticated by the non-default <c>"ApiKey"</c>
/// scheme (X-APIKey header), authorized by the per-endpoint <c>Integration.Inbound.*</c> scope policy
/// (minted as a permission claim by the API-key auth handler). Rate-limited via the named <c>"inbound"</c>
/// partitioned policy. Thin — delegates to MediatR; the command handlers own company resolution,
/// share-group normalization, anti-spoof, the endpoint kill-switch, idempotency and the transactional
/// upsert + SyncLog/IntegrationError + endpoint session update.
/// </summary>
[ApiController]
[Route("api/integration/inbound")]
[Tags("Inbound Integration")]
[EnableRateLimiting("inbound")]
public class InboundIntegrationController : ControllerBase
{
    /// <summary>Optional idempotency header. When absent the handler hashes the canonical payload.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private readonly IMediator _mediator;
    public InboundIntegrationController(IMediator mediator) => _mediator = mediator;

    [HttpPost("payment-terms")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.PaymentTerm")]
    [EndpointSummary("Push payment terms (Infor LN)")]
    [EndpointDescription(@"Upserts Payment Term master rows pushed by Infor LN.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.PaymentTerm` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company (e.g. ""3000""); resolved to a company in the key's tenant and normalized to its share-group source (e.g. 3000 -> 2000).
- **Terms**: 1..1000 records (Code <=20, Description <=200, NetDays 0..365, no duplicate Code).
Behaviour: 200 + UpsertResultDto (per-row outcomes; partial failures flagged + an IntegrationError raised for operator retry); 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> PaymentTerms([FromBody] PushPaymentTermsRequest body, CancellationToken ct)
    {
        var bound = BoundCompanyIds();
        var key = IdempotencyKey();
        var data = await _mediator.Send(new UpsertPaymentTermsCommand(body, bound, key), ct);
        return Result<UpsertResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.DeliveryTerm")]
    [EndpointSummary("Push delivery terms (Infor LN)")]
    [EndpointDescription(@"Upserts Delivery Term master rows pushed by Infor LN.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.DeliveryTerm` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company; resolved + normalized to its share-group source.
- **Terms**: 1..1000 records (Code <=20, Description <=200, no duplicate Code).
Behaviour: 200 + UpsertResultDto; 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> DeliveryTerms([FromBody] PushDeliveryTermsRequest body, CancellationToken ct)
    {
        var bound = BoundCompanyIds();
        var key = IdempotencyKey();
        var data = await _mediator.Send(new UpsertDeliveryTermsCommand(body, bound, key), ct);
        return Result<UpsertResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ---------------- Reference masters — tenant-scoped (no CompanyCode) ----------------

    [HttpPost("currencies")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.Currency")]
    [EndpointSummary("Push currencies (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Currencies([FromBody] PushCurrenciesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCurrenciesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("countries")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.Country")]
    [EndpointSummary("Push countries (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Countries([FromBody] PushCountriesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCountriesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("states")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.State")]
    [EndpointSummary("Push states (Infor LN)")]
    public async Task<Result<UpsertResultDto>> States([FromBody] PushStatesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertStatesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("cities")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.City")]
    [EndpointSummary("Push cities (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Cities([FromBody] PushCitiesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCitiesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("postal-codes")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.PostalCode")]
    [EndpointSummary("Push postal codes (Infor LN)")]
    public async Task<Result<UpsertResultDto>> PostalCodes([FromBody] PushPostalCodesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertPostalCodesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    // ---------------- Inventory masters — company-scoped (CompanyCode) ----------------

    [HttpPost("units")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.Unit")]
    [EndpointSummary("Push units (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Units([FromBody] PushUnitsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertUnitsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("item-groups")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.ItemGroup")]
    [EndpointSummary("Push item groups (Infor LN)")]
    public async Task<Result<UpsertResultDto>> ItemGroups([FromBody] PushItemGroupsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertItemGroupsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("items")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = "Integration.Inbound.Item")]
    [EndpointSummary("Push items (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Items([FromBody] PushItemsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertItemsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    /// <summary>
    /// The key's bound source companies — every "tenantEntityId" claim minted by the API-key auth handler
    /// (Feature C — multi-company keys). The inbound write path requires the resolved incoming source to
    /// be in this set.
    /// </summary>
    private HashSet<Guid> BoundCompanyIds()
        => User.FindAll("tenantEntityId")
            .Select(c => Guid.TryParse(c.Value, out var g) ? (Guid?)g : null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToHashSet();

    private string? IdempotencyKey()
        => Request.Headers.TryGetValue(IdempotencyHeader, out var v) ? v.FirstOrDefault() : null;
}
